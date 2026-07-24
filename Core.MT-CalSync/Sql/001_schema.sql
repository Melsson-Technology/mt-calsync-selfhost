-- MT-CalSync engine schema (baseline).
--
-- Apply once to a fresh MySQL/MariaDB database:
--   mysql -h <host> -u <user> -p <db> < 001_schema.sql
-- (load-schema.sh applies Sql/*.sql in numeric order.) Plain CREATE TABLE, no
-- migration tracking; re-running on a populated DB errors on existing objects.
--
-- Self-host uses the built-in tenant id 1 for the userID/customerID columns, which
-- have NO foreign keys here so the engine schema applies standalone. The hosted SaaS
-- layer adds the identity/billing tables (and the oauth_account -> user/customer FKs)
-- on top.

-- oauth_account: a user's OAuth grant for one external account (Microsoft or Google).
-- Holds the encrypted refresh token (durable) + a cached access token; many
-- provider_connection rows (one per calendar) can share one grant.
CREATE TABLE IF NOT EXISTS oauth_account (
    oauthAccountID       BIGINT AUTO_INCREMENT PRIMARY KEY,
    userID               BIGINT NOT NULL,
    customerID           BIGINT NOT NULL,
    provider             VARCHAR(16)  NOT NULL,            -- 'm365' | 'google'
    providerAccountId    VARCHAR(128) NOT NULL,            -- immutable id: Entra oid / Google sub
    tenantId             VARCHAR(64)  NULL,                -- Entra home tenant
    principalEmail       VARCHAR(320) NOT NULL DEFAULT '',
    displayName          VARCHAR(255) NOT NULL DEFAULT '',
    scopesGranted        TEXT NULL,
    refreshTokenEnc      TEXT NULL,                        -- Encryption v2 ciphertext
    accessTokenEnc       TEXT NULL,                        -- cached access token
    accessTokenExpiresAt DATETIME NULL,
    status               VARCHAR(24) NOT NULL DEFAULT 'connected',   -- connected | needs_reauth
    lastRefreshAt        DATETIME NULL,
    lastReauthNotifyAt   DATETIME NULL,                    -- dedupe for the reconnect email
    backoffUntil         DATETIME NULL,                    -- account-wide throttle wait
    lastError            TEXT NULL,
    dateCreated          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    dateLastModified     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_oauth_account (provider, providerAccountId),
    INDEX idx_oauth_user (userID)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- provider_connection: one calendar endpoint on one provider. authKind selects how
-- credentials are built -- 'app_default' (global app creds) or 'delegated_oauth'
-- (per-user grant via oauthAccountID). credentialRef names the encrypted setting.
CREATE TABLE IF NOT EXISTS provider_connection (
    connectionID        BIGINT AUTO_INCREMENT PRIMARY KEY,
    userID              BIGINT NULL,
    customerID          BIGINT NULL,
    provider            VARCHAR(16)  NOT NULL,           -- 'm365' | 'google'
    displayName         VARCHAR(255) NOT NULL DEFAULT '',
    principalEmail      VARCHAR(320) NOT NULL,           -- mailbox (M365) / impersonated user (Google)
    calendarId          VARCHAR(512) NOT NULL DEFAULT 'primary',
    m365TenantId        VARCHAR(64)  NULL,
    m365ClientId        VARCHAR(64)  NULL,
    credentialRef       VARCHAR(100) NULL,               -- settingName of the encrypted secret (optional)
    authKind            VARCHAR(24)  NOT NULL DEFAULT 'app_default',
    oauthAccountID      BIGINT NULL,
    enabled             TINYINT(1)   NOT NULL DEFAULT 1,
    dateCreated         DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    dateLastModified    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_conn (provider, principalEmail, calendarId(191)),
    INDEX idx_conn_provider (provider, enabled),
    INDEX idx_conn_user (userID),
    CONSTRAINT fk_conn_oauth FOREIGN KEY (oauthAccountID) REFERENCES oauth_account(oauthAccountID)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- sync_pair: a link between one M365 (left) and one Google (right) connection.
CREATE TABLE IF NOT EXISTS sync_pair (
    pairID              BIGINT AUTO_INCREMENT PRIMARY KEY,
    userID              BIGINT NULL,
    customerID          BIGINT NULL,
    name                VARCHAR(255) NOT NULL DEFAULT '',
    leftConnectionID    BIGINT NOT NULL,                 -- M365
    rightConnectionID   BIGINT NOT NULL,                 -- Google
    direction           VARCHAR(16) NOT NULL DEFAULT 'bidirectional',  -- bidirectional|left_to_right|right_to_left
    fidelityMode        VARCHAR(16) NOT NULL DEFAULT 'full_detail',    -- full_detail|busy_block
    recurrenceMode      VARCHAR(16) NOT NULL DEFAULT 'instance',       -- instance|series (series needs full_detail)
    windowDays          INT NOT NULL DEFAULT 60,         -- forward rolling window
    lookbackDays        INT NOT NULL DEFAULT 1,          -- small backward margin
    copyAttendeesToBody TINYINT(1) NOT NULL DEFAULT 0,   -- full_detail: embed attendee names in body
    copyTitle           TINYINT(1) NOT NULL DEFAULT 0,   -- busy_block: copy subject vs literal 'Busy'
    maxWritesPerRun     INT NOT NULL DEFAULT 25,         -- circuit-breaker threshold
    fullResyncHour      TINYINT NOT NULL DEFAULT 3,      -- daily UTC hour to force a full reconcile
    enabled             TINYINT(1) NOT NULL DEFAULT 1,
    nextRunAt           DATETIME NULL,                   -- scheduler: pair is due when null or past
    runIntervalSeconds  INT NOT NULL DEFAULT 300,        -- base cadence (jitter/backoff applied on top)
    lastAlertAt         DATETIME NULL,                   -- throttles owner/operator alert email (1/day)
    dateCreated         DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    dateLastModified    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_pair_enabled (enabled),
    CONSTRAINT fk_pair_left  FOREIGN KEY (leftConnectionID)  REFERENCES provider_connection(connectionID),
    CONSTRAINT fk_pair_right FOREIGN KEY (rightConnectionID) REFERENCES provider_connection(connectionID),
    INDEX idx_pair_user (userID),
    INDEX idx_pair_due (enabled, nextRunAt)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- event_mapping: the crux cross-reference. SHA-256 *EventKey columns carry the unique
-- keys because raw provider ids exceed MySQL's utf8mb4 index byte limit (3072 bytes);
-- the single-column key indexes serve the cross-pair foreign-mirror guard.
CREATE TABLE IF NOT EXISTS event_mapping (
    mappingID               BIGINT AUTO_INCREMENT PRIMARY KEY,
    pairID                  BIGINT NOT NULL,
    originProvider          VARCHAR(16) NOT NULL,        -- authoritative source: 'm365'|'google'
    leftEventId             VARCHAR(768)  NULL,
    leftEventKey            CHAR(64)      NULL,          -- SHA-256(leftEventId)
    leftICalUid             VARCHAR(512)  NULL,
    leftEtag                VARCHAR(128)  NULL,
    leftSeriesMasterId      VARCHAR(768)  NULL,
    rightEventId            VARCHAR(1024) NULL,
    rightEventKey           CHAR(64)      NULL,          -- SHA-256(rightEventId)
    rightICalUid            VARCHAR(512)  NULL,
    rightEtag               VARCHAR(128)  NULL,
    rightRecurringEventId   VARCHAR(1024) NULL,
    unitKind                VARCHAR(16) NOT NULL DEFAULT 'single',  -- single|series_master|occurrence
    originSeriesKey         VARCHAR(768) NULL,
    occurrenceOriginalStart DATETIME NULL,               -- UTC ORIGINAL start (stable when moved)
    projectedHash           CHAR(64) NULL,               -- hash in the mirror stamp (echo discriminator)
    originContentHash       CHAR(64) NULL,               -- did the source functionally change?
    status                  VARCHAR(16) NOT NULL DEFAULT 'active',  -- active|tombstoned
    mirrorVersion           BIGINT NOT NULL DEFAULT 0,
    lastSyncedAt            DATETIME NULL,
    lastSyncDirection       VARCHAR(16) NULL,
    tombstonedAt            DATETIME NULL,
    dateCreated             DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    dateLastModified        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_map_left   (pairID, leftEventKey),
    UNIQUE KEY uq_map_right  (pairID, rightEventKey),
    INDEX idx_map_series     (pairID, originSeriesKey(191), occurrenceOriginalStart),
    INDEX idx_map_left_ical  (pairID, leftICalUid(191)),
    INDEX idx_map_right_ical (pairID, rightICalUid(191)),
    INDEX idx_map_status     (pairID, status),
    INDEX idx_map_left_key   (leftEventKey),
    INDEX idx_map_right_key  (rightEventKey),
    CONSTRAINT fk_map_pair FOREIGN KEY (pairID) REFERENCES sync_pair(pairID) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- sync_state: per (pair, provider) delta/sync tokens + window bookkeeping. windowStart/
-- End matter because a Graph deltaLink is bound to the calendarView window it was minted
-- for and does not auto-extend as the rolling window slides forward.
CREATE TABLE IF NOT EXISTS sync_state (
    syncStateID          BIGINT AUTO_INCREMENT PRIMARY KEY,
    pairID               BIGINT NOT NULL,
    provider             VARCHAR(16) NOT NULL,           -- 'm365'|'google'
    deltaLink            MEDIUMTEXT NULL,                -- Graph calendarView delta link
    syncToken            MEDIUMTEXT NULL,                -- Google nextSyncToken
    windowStart          DATETIME NULL,
    windowEnd            DATETIME NULL,
    lastFullResyncAt     DATETIME NULL,
    lastSuccessfulRunAt  DATETIME NULL,
    consecutiveFailures  INT NOT NULL DEFAULT 0,
    lastError            TEXT NULL,
    dateLastModified     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_state (pairID, provider),
    CONSTRAINT fk_state_pair FOREIGN KEY (pairID) REFERENCES sync_pair(pairID) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- sync_run: per-run audit + counters.
CREATE TABLE IF NOT EXISTS sync_run (
    runID            BIGINT AUTO_INCREMENT PRIMARY KEY,
    pairID           BIGINT NOT NULL,
    triggerType      VARCHAR(16) NOT NULL DEFAULT 'timer',        -- timer|manual
    syncType         VARCHAR(16) NOT NULL DEFAULT 'incremental',  -- incremental|full_resync
    startedAt        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finishedAt       DATETIME NULL,
    status           VARCHAR(24) NOT NULL DEFAULT 'running',
    leftChanges      INT NOT NULL DEFAULT 0,
    rightChanges     INT NOT NULL DEFAULT 0,
    createdCount     INT NOT NULL DEFAULT 0,
    updatedCount     INT NOT NULL DEFAULT 0,
    deletedCount     INT NOT NULL DEFAULT 0,
    skippedCount     INT NOT NULL DEFAULT 0,
    echoSkippedCount INT NOT NULL DEFAULT 0,
    conflictCount    INT NOT NULL DEFAULT 0,
    adoptedCount     INT NOT NULL DEFAULT 0,
    deadLetteredCount INT NOT NULL DEFAULT 0,
    errorText        TEXT NULL,
    INDEX idx_run_pair (pairID, startedAt),
    CONSTRAINT fk_run_pair FOREIGN KEY (pairID) REFERENCES sync_pair(pairID) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- dead_letter: items that fail permanently/repeatedly. The UNIQUE key makes a re-failing
-- item bump attemptCount instead of spamming rows; open rows are skipped so one poison
-- event can't fail the whole pair.
CREATE TABLE IF NOT EXISTS dead_letter (
    deadLetterID     BIGINT AUTO_INCREMENT PRIMARY KEY,
    pairID           BIGINT NOT NULL,
    mappingID        BIGINT NULL,
    sourceProvider   VARCHAR(16) NOT NULL,
    sourceEventId    VARCHAR(1024) NULL,
    sourceEventKey   CHAR(64) NULL,                      -- SHA-256(sourceEventId)
    operation        VARCHAR(16) NOT NULL,               -- create|update|delete
    payloadJson      MEDIUMTEXT NULL,
    errorCode        VARCHAR(64) NULL,
    errorText        TEXT NULL,
    attemptCount     INT NOT NULL DEFAULT 1,
    firstFailedAt    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lastFailedAt     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    resolved         TINYINT(1) NOT NULL DEFAULT 0,
    resolvedAt       DATETIME NULL,
    UNIQUE KEY uq_dl (pairID, sourceProvider, sourceEventKey, operation),
    INDEX idx_dl_open (pairID, resolved),
    CONSTRAINT fk_dl_pair FOREIGN KEY (pairID) REFERENCES sync_pair(pairID) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- sync_lock: lease-based per-pair advisory lock (compare-and-set on leaseExpiresAt).
-- Used instead of GET_LOCK() because DataAccess opens a fresh connection per call.
CREATE TABLE IF NOT EXISTS sync_lock (
    pairID           BIGINT PRIMARY KEY,
    lockedBy         VARCHAR(128) NULL,                  -- host:pid:guid of the holding run
    lockedAt         DATETIME NULL,
    leaseExpiresAt   DATETIME NULL,
    CONSTRAINT fk_lock_pair FOREIGN KEY (pairID) REFERENCES sync_pair(pairID) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- settings: DB-backed runtime settings (AES-encrypted secret overrides + runtime flags).
CREATE TABLE IF NOT EXISTS settings (
    settingID            BIGINT AUTO_INCREMENT PRIMARY KEY,
    settingName          VARCHAR(100) UNIQUE NOT NULL,
    settingValue         TEXT,
    dateLastModified     DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
