-- sqldbgr runtime-schema. Skapas idempotent per databas.
-- Allt här körs i användarens egen databas och är öppen källkod (se NOTICE.md).

IF SCHEMA_ID(N'__dbg') IS NULL
    EXEC(N'CREATE SCHEMA __dbg');
GO

IF OBJECT_ID(N'__dbg.Control') IS NULL
CREATE TABLE __dbg.Control (
    SessionId         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    ActiveBreakpoints NVARCHAR(MAX)    NULL,          -- JSON-array av stmt_id
    Command           NVARCHAR(20)     NOT NULL DEFAULT 'entry', -- continue|stepOver|stepIn|entry|abort
    Signaled          BIT              NOT NULL DEFAULT 0,
    PausedAtStmt      INT              NULL,          -- NULL = kör, annars pausad vid stmt
    PauseSeq          INT              NOT NULL DEFAULT 0, -- stegas per paus; monitorn jämför denna
    LastHeartbeatUtc  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Migrering av äldre installationer
IF COL_LENGTH(N'__dbg.Control', N'PauseSeq') IS NULL
    ALTER TABLE __dbg.Control ADD PauseSeq INT NOT NULL DEFAULT 0;
GO

IF OBJECT_ID(N'__dbg.Locals') IS NULL
CREATE TABLE __dbg.Locals (
    SessionId UNIQUEIDENTIFIER NOT NULL,
    Name      NVARCHAR(128)    NOT NULL,
    TypeName  NVARCHAR(128)    NOT NULL,
    Value     NVARCHAR(MAX)    NULL,
    INDEX IX_Locals_Session (SessionId)
);
GO

-- Billig förkontroll som instrumenteringen anropar före varje statement:
-- bara när den svarar 1 görs den dyra capturen av tabellvariabler och
-- anropet till __dbg.Pause. 'abort' ger 1 så Pause hinner kasta.
CREATE OR ALTER FUNCTION __dbg.ShouldPause(@sid UNIQUEIDENTIFIER, @stmt_id INT)
RETURNS BIT
AS
BEGIN
    IF @sid IS NULL RETURN 0;
    DECLARE @cmd NVARCHAR(20), @bp NVARCHAR(MAX);
    SELECT @cmd = Command, @bp = ActiveBreakpoints
    FROM __dbg.Control WHERE SessionId = @sid;
    IF @cmd IS NULL RETURN 0;
    IF @cmd <> 'continue' RETURN 1;
    IF @bp IS NOT NULL AND EXISTS (SELECT 1 FROM OPENJSON(@bp) WHERE value = @stmt_id) RETURN 1;
    RETURN 0;
END
GO

CREATE OR ALTER PROCEDURE __dbg.Pause
    @stmt_id INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @sid UNIQUEIDENTIFIER =
        CONVERT(UNIQUEIDENTIFIER, SESSION_CONTEXT(N'__dbg_session'));
    IF @sid IS NULL RETURN;  -- ej debug-markerad session: kör obehindrat

    DECLARE @cmd NVARCHAR(20), @bp NVARCHAR(MAX);
    SELECT @cmd = Command, @bp = ActiveBreakpoints
    FROM __dbg.Control WHERE SessionId = @sid;

    IF @cmd IS NULL RETURN;  -- ingen kontrollrad: kör obehindrat

    -- 'abort' (Stop i klienten): döda batchen här, inga fler statements körs.
    IF @cmd = 'abort' THROW 50099, 'sqldbgr: sessionen avbröts', 1;

    -- 'continue': pausa bara vid breakpoint. 'stepOver'/'stepIn'/'entry': pausa alltid.
    IF @cmd = 'continue'
       AND (@bp IS NULL OR NOT EXISTS (
            SELECT 1 FROM OPENJSON(@bp) WHERE value = @stmt_id))
        RETURN;

    -- Markera pausad och vänta på signal från klienten. PauseSeq stegas så
    -- monitorn ser även upprepade pauser på samma statement (loopar).
    UPDATE __dbg.Control SET PausedAtStmt = @stmt_id, Signaled = 0, PauseSeq = PauseSeq + 1
    WHERE SessionId = @sid;

    DECLARE @signaled BIT = 0;
    WHILE @signaled = 0
    BEGIN
        WAITFOR DELAY '00:00:00.050';
        SELECT @signaled = Signaled, @cmd = Command FROM __dbg.Control WHERE SessionId = @sid;
        IF @signaled IS NULL RETURN; -- sessionen städad utifrån: släpp igenom
    END

    UPDATE __dbg.Control SET Signaled = 0, PausedAtStmt = NULL
    WHERE SessionId = @sid;

    IF @cmd = 'abort' THROW 50099, 'sqldbgr: sessionen avbröts', 1;
END
GO
