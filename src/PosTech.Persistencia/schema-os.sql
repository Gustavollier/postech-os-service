-- ---------------------------------------------------------------------------
-- Schema do os-db: a ordem de serviço e o histórico de status.
--
-- O estado da saga e o outbox estão em schema-mensageria.sql, no mesmo banco.
-- Ficam juntos de propósito: a saga e a ordem mudam na mesma transação, e
-- separá-las criaria a transação distribuída que o padrão existe para evitar.
--
-- Idempotente: pode ser reaplicado sobre um ambiente já provisionado.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OrdemServico')
BEGIN
    CREATE TABLE OrdemServico (
        Id INT PRIMARY KEY IDENTITY(1,1),

        -- Ids de outros serviços. Sem FK, de propósito: Cliente e Veiculo vivem
        -- no identidade-db e Funcionario também. Uma FK aqui seria exatamente o
        -- acesso cruzado que o enunciado proíbe.
        IdCliente INT NOT NULL,
        IdVeiculo INT NOT NULL,
        IdFuncionario INT NOT NULL,

        -- 0 Recebida, 1 EmDiagnostico, 2 AguardandoAprovacao,
        -- 3 EmExecucao, 4 Finalizada, 5 Entregue
        Status TINYINT NOT NULL DEFAULT 0,

        -- Denormalizado do evento OrdemAberta. Evita a listagem de ordens virar
        -- uma chamada ao Identidade por linha (ADR-0008).
        NomeCliente NVARCHAR(150) NULL,
        DescricaoVeiculo NVARCHAR(150) NULL,

        CriadoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        AtualizadoEm DATETIME2 NULL
    );
END;
GO

-- A listagem ordenada por status é a tela mais usada da recepção.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OrdemServico_Status')
BEGIN
    CREATE NONCLUSTERED INDEX IX_OrdemServico_Status ON OrdemServico (Status, CriadoEm);
END;
GO

-- O cliente só enxerga as ordens dele: a checagem de posse filtra por aqui.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OrdemServico_Cliente')
BEGIN
    CREATE NONCLUSTERED INDEX IX_OrdemServico_Cliente ON OrdemServico (IdCliente, CriadoEm);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'StatusHistorico')
BEGIN
    CREATE TABLE StatusHistorico (
        Id BIGINT PRIMARY KEY IDENTITY(1,1),
        IdOrdem INT NOT NULL,
        StatusAnterior TINYINT NULL,
        StatusNovo TINYINT NOT NULL,
        -- Por que mudou. Numa compensação, é aqui que fica o motivo que o
        -- cliente vai ler na tela.
        Motivo NVARCHAR(500) NULL,
        OcorridoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT FK_StatusHistorico_Ordem FOREIGN KEY (IdOrdem) REFERENCES OrdemServico (Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StatusHistorico_Ordem')
BEGIN
    CREATE NONCLUSTERED INDEX IX_StatusHistorico_Ordem ON StatusHistorico (IdOrdem, OcorridoEm);
END;
GO
