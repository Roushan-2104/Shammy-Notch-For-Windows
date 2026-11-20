-- One row per problem (same error in the same place), with its two GitHub issues and a counter.
CREATE TABLE IF NOT EXISTS problems (
  signature     TEXT PRIMARY KEY,
  kind          TEXT NOT NULL,
  error         TEXT NOT NULL,
  area          TEXT NOT NULL,
  private_issue INTEGER,
  public_issue  INTEGER,
  total         INTEGER NOT NULL DEFAULT 1,
  first_seen    TEXT NOT NULL,
  last_seen     TEXT NOT NULL,
  versions      TEXT NOT NULL DEFAULT '[]'
);

-- Repeat reports, waiting for the daily digest; deleted a month after they were sent on.
CREATE TABLE IF NOT EXISTS reports (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  signature   TEXT NOT NULL,
  received_at TEXT NOT NULL,
  version     TEXT NOT NULL,
  windows     TEXT,
  note        TEXT,
  log         TEXT,
  digested    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS reports_waiting ON reports (signature, digested);

-- Values worked out once a day: the newest released version, and the day's random salt for connection hashes.
CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  day   TEXT NOT NULL
);

-- Today's counts for the abuse limits. Connections are kept only as a hash that changes every day.
CREATE TABLE IF NOT EXISTS limits (
  key   TEXT PRIMARY KEY,
  day   TEXT NOT NULL,
  count INTEGER NOT NULL
);
