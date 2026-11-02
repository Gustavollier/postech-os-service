using System.Text.Json.Serialization;
using PosTech.Mensageria;
using PosTech.Os.Api.Aplicacao;
using PosTech.Persistencia;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("SqlConnectionString não configurada.");

var rabbitConnectionString = builder.Configuration["RabbitMqConnectionString"]
    ?? throw new InvalidOperationException("RabbitMqConnectionString não configurada.");

// Falhar na subida é melhor que subir sem conseguir falar com nada e descobrir
// na primeira requisição. É a mesma checagem que a Auth Function faz.

builder.Services.AddControllers()
    .AddJsonOptions(opcoes =>
    {
        // Enum por nome na API, igual ao formato do fio. Na Fase 3 a API
        // serializava enum por nome e o front lia como número, e isso custou
        // três bugs — um deles deixou o botão de aprovar orçamento sem
        // renderizar. Mesma convenção nos dois lados, sempre.
        opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

// Logs em JSON para o Datadog parsear sem regra custom, como na Fase 3.
builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
});

builder.Services.AddScoped<IOrdemRepositorio>(_ => new OrdemRepositorio(connectionString));
builder.Services.AddScoped<ISagaRepositorio>(_ => new SagaRepositorio(connectionString));
builder.Services.AddScoped<ManipuladorDaSaga>();

builder.Services.AddSingleton<IRepositorioDeOutbox>(_ => new OutboxRepositorio(connectionString));
builder.Services.AddSingleton<IRegistroDeIdempotencia>(_ => new IdempotenciaRepositorio(connectionString));

// Uma conexão AMQP por processo: ela é cara e feita para durar.
builder.Services.AddSingleton(_ => new ConexaoRabbitMq(rabbitConnectionString, ManipuladorDaSaga.Origem));
builder.Services.AddSingleton<IPublicadorDeEventos>(sp =>
    new PublicadorRabbitMq(sp.GetRequiredService<ConexaoRabbitMq>()));
builder.Services.AddSingleton(sp => new ProcessadorDoOutbox(
    sp.GetRequiredService<IRepositorioDeOutbox>(),
    sp.GetRequiredService<IPublicadorDeEventos>()));

builder.Services.AddHostedService<PublicadorDoOutbox>();
builder.Services.AddHostedService<ConsumidorDaSaga>();
builder.Services.AddHostedService<VarredorDeTimeout>();

var app = builder.Build();

// O schema é idempotente e embutido no assembly. Aplicar aqui tira do caminho
// o passo manual que cada ambiente novo exigia.
await InstaladorDoSchema.AplicarAsync(connectionString);

// Swagger ligado também fora de desenvolvimento: é entregável da fase, e o
// APIM termina o TLS antes do tráfego chegar aqui.
app.UseSwagger();
app.UseSwaggerUI();

// Sem HttpsRedirection: o gateway já terminou o TLS, e redirecionar aqui faria
// o APIM receber 307 em vez da resposta (ADR-0003 da Fase 3).

app.MapControllers();
app.MapHealthChecks("/health");

// Pronto para receber tráfego. Separado do /health porque o Kubernetes precisa
// distinguir "vivo" de "pronto": o pod sobe antes do consumidor conectar no
// broker, e mandar tráfego nesse intervalo dá erro à toa.
app.MapGet("/health/ready", () => Results.Ok(new { status = "pronto" }));

app.Run();

/// <summary>Exposto para os testes de integração subirem a API em memória.</summary>
public partial class Program;
