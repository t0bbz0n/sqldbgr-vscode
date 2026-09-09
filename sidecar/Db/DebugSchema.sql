-- sqldbgr runtime-schema. Skapas idempotent per databas.
-- Everything here runs in the user's own database and is open source (NOTICE.md).
--
-- Locking: the batch can sit paused inside a user transaction. So Control is
-- written ONLY by the sidecar, and read by the batch with NOLOCK, while
-- PauseState is written ONLY by __dbg.Pause, and read by the sidecar with
-- NOLOCK. If Pause wrote to Control, the transaction's exclusive lock would
-- block the sidecar's next signal and the session would hang.

IF SCHEMA_ID(N'__dbg') IS NULL
    EXEC(N'CREATE SCHEMA __dbg');
GO

IF OBJECT_ID(N'__dbg.Control') IS NULL
CREATE TABLE __dbg.Control (
    SessionId         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Command           NVARCHAR(20)     NOT NULL DEFAULT 'continue', -- continue|stepOver|stepIn|entry|abort
    SignalSeq         INT              NOT NULL DEFAULT 0,          -- stepped once per signal from the sidecar
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
    PausedAtStmt INT              NULL,          -- NULL means running, otherwise paused at that statement
    PauseSeq     INT              NOT NULL DEFAULT 0 -- stepped once per pause; the sidecar compares it
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

-- Variable values set from the client (setVariable). The batch reads them after
-- the next pause and empties the table. A heap with no unique index, so the
-- sidecar's INSERT never collides with the batch's uncommitted DELETE.
IF OBJECT_ID(N'__dbg.Overrides') IS NULL
CREATE TABLE __dbg.Overrides (
    SessionId UNIQUEIDENTIFIER NOT NULL,
    Name      NVARCHAR(128)    NOT NULL,
    Value     NVARCHAR(MAX)    NULL,
    INDEX IX_Overrides_Session (SessionId)
);
GO

-- A cheap pre-check the instrumentation calls before every statement. Only when
-- it answers 1 is the expensive capture of table variables done, and __dbg.Pause
-- called. 'abort' answers 1 so that Pause gets a chance to throw.
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
    IF @sid IS NULL RETURN;  -- not a debug session: run unhindered

    DECLARE @cmd NVARCHAR(20), @bp NVARCHAR(MAX), @seen INT, @sig INT, @hb DATETIME2;
    SELECT @cmd = Command, @bp = ActiveBreakpoints, @seen = SignalSeq, @hb = LastHeartbeatUtc
    FROM __dbg.Control WITH (NOLOCK) WHERE SessionId = @sid;

    IF @cmd IS NULL RETURN;  -- no control row: run unhindered

    -- 'abort', which is Stop in the client: kill the batch here, nothing more runs.
    IF @cmd = 'abort' THROW 50099, 'sqldbgr: session aborted', 1;

    -- 'continue' pauses only at a breakpoint. 'stepOver', 'stepIn' and 'entry' always pause.
    IF @cmd = 'continue'
       AND (@bp IS NULL OR NOT EXISTS (
            SELECT 1 FROM OPENJSON(@bp) WHERE value = @stmt_id))
        RETURN;

    -- Mark it paused. PauseSeq is stepped so the sidecar also sees repeated
    -- pauses on the same statement, which is what a loop does.
    UPDATE __dbg.PauseState SET PausedAtStmt = @stmt_id, PauseSeq = PauseSeq + 1
    WHERE SessionId = @sid;
    IF @@ROWCOUNT = 0
        INSERT INTO __dbg.PauseState (SessionId, PausedAtStmt, PauseSeq) VALUES (@sid, @stmt_id, 1);

    -- Wait for the next signal, a SignalSeq greater than the one we saw. If the
    -- sidecar dies the heartbeat stops and the batch is aborted, so no lock is
    -- held forever.
    WHILE 1 = 1
    BEGIN
        WAITFOR DELAY '00:00:00.050';
        -- Clear first: SELECT @x = ... with no matching row leaves the variable
        -- as it was, so a deleted control row would otherwise look like an
        -- unchanged signal and an ever-older heartbeat, and the session would
        -- hang until the timeout fired.
        SET @sig = NULL; SET @hb = NULL;
        SELECT @sig = SignalSeq, @cmd = Command, @hb = LastHeartbeatUtc
        FROM __dbg.Control WITH (NOLOCK) WHERE SessionId = @sid;
        IF @sig IS NULL BREAK;         -- the session was cleaned up from outside: let it through
        IF @sig > @seen BREAK;
        IF DATEDIFF(SECOND, @hb, SYSUTCDATETIME()) > 60
            THROW 50098, 'sqldbgr: sidecar stopped responding - session aborted', 1;
    END

    UPDATE __dbg.PauseState SET PausedAtStmt = NULL WHERE SessionId = @sid;

    IF @cmd = 'abort' THROW 50099, 'sqldbgr: session aborted', 1;
END
GO
