-- LedgerMatch schema version 1
-- Dates are 'yyyy-MM-dd', months 'yyyy-MM', timestamps ISO 8601 round-trip ("O"), money integer cents.

CREATE TABLE id_sequence (
    name    TEXT PRIMARY KEY,
    next_id INTEGER NOT NULL
);
INSERT INTO id_sequence (name, next_id) VALUES ('global', 1000);

CREATE TABLE mapping_profile (
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL UNIQUE,
    kind        TEXT NOT NULL,
    config_json TEXT NOT NULL
);

CREATE TABLE account (
    id                    INTEGER PRIMARY KEY,
    name                  TEXT NOT NULL UNIQUE,
    profile_id            INTEGER NOT NULL REFERENCES mapping_profile(id),
    opening_balance_cents INTEGER NOT NULL,
    opening_date          TEXT NOT NULL
);

CREATE TABLE category (
    id        INTEGER PRIMARY KEY,
    name      TEXT NOT NULL UNIQUE,
    is_system INTEGER NOT NULL DEFAULT 0
);
-- The fee category is required by fee matching; it may be renamed but not deleted.
INSERT INTO category (id, name, is_system) VALUES (1, '手續費', 1);

CREATE TABLE import_batch (
    id          INTEGER PRIMARY KEY,
    kind        TEXT NOT NULL,
    account_id  INTEGER REFERENCES account(id),
    file_name   TEXT NOT NULL,
    file_sha256 TEXT NOT NULL,
    imported_at TEXT NOT NULL,
    added       INTEGER NOT NULL,
    skipped     INTEGER NOT NULL,
    errors      INTEGER NOT NULL,
    pending     INTEGER NOT NULL
);

CREATE TABLE bank_txn (
    id              INTEGER PRIMARY KEY,
    account_id      INTEGER NOT NULL REFERENCES account(id),
    txn_date        TEXT NOT NULL,
    posting_period  TEXT NOT NULL,
    amount_cents    INTEGER NOT NULL,
    description     TEXT NOT NULL,
    serial          TEXT,
    note            TEXT,
    dedup_key       TEXT NOT NULL,
    import_batch_id INTEGER REFERENCES import_batch(id),
    source_line     INTEGER,
    category_id     INTEGER REFERENCES category(id),
    classified_by   TEXT NOT NULL,
    book_confirmed  INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_bank_txn_dedup ON bank_txn(account_id, dedup_key);
CREATE INDEX ix_bank_txn_period ON bank_txn(account_id, posting_period, amount_cents);
CREATE INDEX ix_bank_txn_date ON bank_txn(account_id, txn_date);

CREATE TABLE pending_item (
    id              INTEGER PRIMARY KEY,
    account_id      INTEGER NOT NULL REFERENCES account(id),
    txn_date        TEXT NOT NULL,
    amount_cents    INTEGER NOT NULL,
    description     TEXT NOT NULL,
    serial          TEXT,
    note            TEXT,
    dedup_key       TEXT NOT NULL,
    import_batch_id INTEGER NOT NULL REFERENCES import_batch(id),
    source_line     INTEGER NOT NULL,
    status          TEXT NOT NULL,
    posted_txn_id   INTEGER REFERENCES bank_txn(id)
);
CREATE UNIQUE INDEX ux_pending_dedup ON pending_item(account_id, dedup_key);

CREATE TABLE receipt (
    id              INTEGER PRIMARY KEY,
    account_id      INTEGER NOT NULL REFERENCES account(id),
    issue_date      TEXT NOT NULL,
    customer        TEXT NOT NULL,
    receipt_no      TEXT NOT NULL UNIQUE,
    amount_cents    INTEGER NOT NULL CHECK (amount_cents > 0),
    note            TEXT,
    import_batch_id INTEGER REFERENCES import_batch(id)
);
CREATE INDEX ix_receipt_account_date ON receipt(account_id, issue_date);

CREATE TABLE match (
    id             INTEGER PRIMARY KEY,
    deposit_txn_id INTEGER NOT NULL UNIQUE REFERENCES bank_txn(id),
    period         TEXT NOT NULL,
    kind           TEXT NOT NULL,
    confidence     TEXT NOT NULL,
    fee_cents      INTEGER NOT NULL CHECK (fee_cents >= 0),
    note           TEXT
);

CREATE TABLE match_link (
    id         INTEGER PRIMARY KEY,
    match_id   INTEGER NOT NULL REFERENCES match(id),
    receipt_id INTEGER NOT NULL UNIQUE REFERENCES receipt(id),
    period     TEXT NOT NULL
);
CREATE INDEX ix_match_link_match ON match_link(match_id);

CREATE TABLE split_line (
    id           INTEGER PRIMARY KEY,
    txn_id       INTEGER NOT NULL REFERENCES bank_txn(id),
    seq          INTEGER NOT NULL,
    category_id  INTEGER NOT NULL REFERENCES category(id),
    amount_cents INTEGER NOT NULL,
    weight       TEXT,
    period       TEXT NOT NULL
);
CREATE INDEX ix_split_line_txn ON split_line(txn_id);

CREATE TABLE rule (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    priority      INTEGER NOT NULL,
    desc_contains TEXT,
    desc_regex    TEXT,
    min_cents     INTEGER,
    max_cents     INTEGER,
    account_id    INTEGER REFERENCES account(id),
    category_id   INTEGER NOT NULL REFERENCES category(id),
    enabled       INTEGER NOT NULL
);

CREATE TABLE period (
    id        INTEGER PRIMARY KEY,
    month     TEXT NOT NULL UNIQUE,
    is_closed INTEGER NOT NULL,
    closed_at TEXT
);

CREATE TABLE period_event (
    id     INTEGER PRIMARY KEY,
    month  TEXT NOT NULL,
    action TEXT NOT NULL,
    reason TEXT,
    at     TEXT NOT NULL
);

CREATE TABLE setting (
    id         INTEGER PRIMARY KEY,
    key        TEXT NOT NULL UNIQUE,
    value_json TEXT NOT NULL
);

CREATE TABLE audit_log (
    seq         INTEGER PRIMARY KEY,
    at          TEXT NOT NULL,
    operation   TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_id   INTEGER NOT NULL,
    action      TEXT NOT NULL,
    before_json TEXT,
    after_json  TEXT,
    prev_hash   TEXT NOT NULL,
    hash        TEXT NOT NULL
);
CREATE INDEX ix_audit_entity ON audit_log(entity_type, entity_id);

-- Audit log is append-only.
CREATE TRIGGER trg_audit_no_update BEFORE UPDATE ON audit_log
BEGIN SELECT RAISE(ABORT, 'AUDIT_APPEND_ONLY'); END;
CREATE TRIGGER trg_audit_no_delete BEFORE DELETE ON audit_log
BEGIN SELECT RAISE(ABORT, 'AUDIT_APPEND_ONLY'); END;

-- Second line of defence for closed periods (the application checks first).
CREATE TRIGGER trg_bank_txn_closed_ins BEFORE INSERT ON bank_txn
WHEN EXISTS (SELECT 1 FROM period WHERE month = NEW.posting_period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_bank_txn_closed_upd BEFORE UPDATE ON bank_txn
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (OLD.posting_period, NEW.posting_period) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_bank_txn_closed_del BEFORE DELETE ON bank_txn
WHEN EXISTS (SELECT 1 FROM period WHERE month = OLD.posting_period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;

CREATE TRIGGER trg_receipt_closed_ins BEFORE INSERT ON receipt
WHEN EXISTS (SELECT 1 FROM period WHERE month = substr(NEW.issue_date, 1, 7) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_receipt_closed_upd BEFORE UPDATE ON receipt
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (substr(OLD.issue_date, 1, 7), substr(NEW.issue_date, 1, 7)) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_receipt_closed_del BEFORE DELETE ON receipt
WHEN EXISTS (SELECT 1 FROM period WHERE month = substr(OLD.issue_date, 1, 7) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;

CREATE TRIGGER trg_match_closed_ins BEFORE INSERT ON match
WHEN EXISTS (SELECT 1 FROM period WHERE month = NEW.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_match_closed_upd BEFORE UPDATE ON match
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (OLD.period, NEW.period) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_match_closed_del BEFORE DELETE ON match
WHEN EXISTS (SELECT 1 FROM period WHERE month = OLD.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;

CREATE TRIGGER trg_match_link_closed_ins BEFORE INSERT ON match_link
WHEN EXISTS (SELECT 1 FROM period WHERE month = NEW.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_match_link_closed_del BEFORE DELETE ON match_link
WHEN EXISTS (SELECT 1 FROM period WHERE month = OLD.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;

CREATE TRIGGER trg_split_line_closed_ins BEFORE INSERT ON split_line
WHEN EXISTS (SELECT 1 FROM period WHERE month = NEW.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_split_line_closed_upd BEFORE UPDATE ON split_line
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (OLD.period, NEW.period) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_split_line_closed_del BEFORE DELETE ON split_line
WHEN EXISTS (SELECT 1 FROM period WHERE month = OLD.period AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
