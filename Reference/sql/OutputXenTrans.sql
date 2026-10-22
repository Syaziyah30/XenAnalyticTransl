-- ============================================================================
-- XenCreator.dbo.OutputXenTrans
--
-- Saved AI translations, loaded by the Studio's Load from DB button without calling
-- the AI. One row per Equipment + ScenarioName + OutputTarget: the app replaces the
-- row on every new translation, so no history is kept.
--
-- Run once on 128.100.20.33\neptune by someone with CREATE TABLE rights on
-- XenCreator. Safe to run twice: it does nothing if the table already exists.
-- ============================================================================

USE XenCreator;
GO

IF OBJECT_ID(N'dbo.OutputXenTrans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutputXenTrans (
        Id               INT IDENTITY(1,1) PRIMARY KEY,
        CacheKey         CHAR(64)       NOT NULL,   -- SHA-256 fingerprint of everything sent to the AI
        Equipment        NVARCHAR(50)   NOT NULL,   -- Motor / Valve
        ScenarioName     NVARCHAR(200)  NOT NULL,
        ScenarioText     NVARCHAR(MAX)  NOT NULL,
        OutputTarget     NVARCHAR(30)   NOT NULL,   -- Pseudocode / Logic Expression / SCL
        Provider         NVARCHAR(100)  NOT NULL,
        Model            NVARCHAR(100)  NOT NULL,
        Code             NVARCHAR(MAX)  NOT NULL,   -- the generated logic
        Explanation      NVARCHAR(MAX)  NULL,
        VariablesUsed    NVARCHAR(MAX)  NULL,       -- JSON list; lets SCL results be re-checked on load
        Warnings         NVARCHAR(MAX)  NULL,       -- JSON list
        PromptTokens     INT            NULL,
        CompletionTokens INT            NULL,
        ElapsedMs        INT            NULL,
        CreatedAt        DATETIME2      NOT NULL CONSTRAINT DF_OutputXenTrans_CreatedAt DEFAULT SYSUTCDATETIME()
    );

    -- Lookups are "newest row for this fingerprint".
    CREATE INDEX IX_OutputXenTrans_CacheKey
        ON dbo.OutputXenTrans (CacheKey, CreatedAt DESC);
END
GO
