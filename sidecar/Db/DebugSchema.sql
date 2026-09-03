-- sqldbgr runtime-schema. Skapas idempotent per databas.
-- Allt här körs i användarens egen databas och är öppen källkod (se NOTICE.md).
--
-- Låsdesign: batchen kan stå pausad inne i en användartransaktion. Därför
-- skrivs Control BARA av sidecaren (läses av batchen med NOLOCK) och
-- PauseState BARA av __dbg.Pause (läses av sidecaren med NOLOCK). Skulle
-- Pause skriva i Control skulle transaktionens X-lås blockera sidecarens
-- nästa signal och sessionen hänga.

IF SCHEMA_ID(N'__dbg') IS NULL
    EXEC(N'CREATE SCHEMA __dbg');
GO

IF OBJECT_ID(N'__dbg.Control') IS NULL
CREATE TABLE __dbg.Control (
    SessionId         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Command           NVARCHAR(20)     NOT NULL DEFAULT 'continue', -- continue|stepOver|stepIn|entry|abort
    SignalSeq         INT              NOT NULL DEFAULT 0,          -- stegas per signal från sidecaren
    ActiveBreakpoints NVARCHAR(MAX)    NULL,                        -- JSON-array av stmt_id
    LastHeartbeatUtc  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
IF COL_LENGTH(N'__dbg.Control', N'SignalSeq') IS NULL
    ALTER TABLE __dbg.Control ADD SignalSeq INT NOT NULL DEFAULT 0;
GO

IF OBJECT_ID(N'__dbg.PauseState') IS NULL
CREATE TABLE __dbg.PauseState (
    SessionId    UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    PausedAtStmt INT              NULL,          -- NULL = kör, annars pausad vid stmt
    PauseSeq     INT              NOT NULL DEFAULT 0 -- stegas per paus; sidecaren jämför denna
);
GO

IF OBJECT_ID(N'__dbg.Locals') IS NULL
CREATE TABLE __dbg.Locals (
    SessionId UNIQUEIDENTIFIER NOT NULL,
    Ordinal   INT              NOT NULL DEFAULT 0, -- deklarationsordning
    Name      NVARCHAR(128)    NOT NULL,
    TypeName  NVARCHAR(128)    NOT NULL,
    Value     NVARCHAR(MAX)    NULL,
    INDEX IX_Locals_Session (SessionId)
);
GO
IF COL_LENGTH(N'__dbg.Locals', N'Ordinal') IS NULL
    ALTER TABLE __dbg.Locals ADD Ordinal INT NOT NULL DEFAULT 0;
GO

-- Variabelvärden satta från klienten (setVariable); batchen läser in dem
-- efter nästa paus och tömmer tabellen. Heap utan unikt index så sidecarens
-- INSERT aldrig krockar med batchens ännu ej committade DELETE.
IF OBJECT_ID(N'__dbg.Overrides') IS NULL
CREATE TABLE __dbg.Overrides (
    SessionId UNIQUEIDENTIFIER NOT NULL,
    Name      NVARCHAR(128)    NOT NULL,
    Value     NVARCHAR(MAX)    NULL,
    INDEX IX_Overrides_Session (SessionId)
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
    FROM __dbg.Control WITH (NOLOCK) WHERE SessionId = @sid;
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

    DECLARE @cmd NVARCHAR(20), @bp NVARCHAR(MAX), @seen INT, @sig INT, @hb DATETIME2;
    SELECT @cmd = Command, @bp = ActiveBreakpoints, @seen = SignalSeq, @hb = LastHeartbeatUtc
    FROM __dbg.Control WITH (NOLOCK) WHERE SessionId = @sid;

    IF @cmd IS NULL RETURN;  -- ingen kontrollrad: kör obehindrat

    -- 'abort' (Stop i klienten): döda batchen här, inga fler statements körs.
    IF @cmd = 'abort' THROW 50099, 'sqldbgr: session aborted', 1;

    -- 'continue': pausa bara vid breakpoint. 'stepOver'/'stepIn'/'entry': pausa alltid.
    IF @cmd = 'continue'
       AND (@bp IS NULL OR NOT EXISTS (
            SELECT 1 FROM OPENJSON(@bp) WHERE value = @stmt_id))
        RETURN;

    -- Markera pausad. PauseSeq stegas så sidecaren ser även upprepade pauser
    -- på samma statement (loopar).
    UPDATE __dbg.PauseState SET PausedAtStmt = @stmt_id, PauseSeq = PauseSeq + 1
    WHERE SessionId = @sid;
    IF @@ROWCOUNT = 0
        INSERT INTO __dbg.PauseState (SessionId, PausedAtStmt, PauseSeq) VALUES (@sid, @stmt_id, 1);

    -- Vänta på nästa signal (SignalSeq > den vi såg). Dör sidecaren slutar
    -- heartbeaten och batchen avbryts så inga lås hålls för evigt.
    WHILE 1 = 1
    BEGIN
        WAITFOR DELAY '00:00:00.050';
        -- Nollställ först: SELECT @x = ... utan träff lämnar variabeln orörd, så
        -- en borttagen kontrollrad skulle annars se ut som oförändrad signal och
        -- en allt äldre heartbeat - sessionen skulle hänga tills timeouten slog.
        SET @sig = NULL; SET @hb = NULL;
        SELECT @sig = SignalSeq, @cmd = Command, @hb = LastHeartbeatUtc
        FROM __dbg.Control WITH (NOLOCK) WHERE SessionId = @sid;
        IF @sig IS NULL BREAK;         -- sessionen städad utifrån: släpp igenom
        IF @sig > @seen BREAK;
        IF DATEDIFF(SECOND, @hb, SYSUTCDATETIME()) > 60
            THROW 50098, 'sqldbgr: sidecar stopped responding - session aborted', 1;
    END

    UPDATE __dbg.PauseState SET PausedAtStmt = NULL WHERE SessionId = @sid;

    IF @cmd = 'abort' THROW 50099, 'sqldbgr: session aborted', 1;
END
GO
