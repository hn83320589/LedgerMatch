-- LedgerMatch schema version 2: book-side manual entries (e.g. cheques not yet cleared) and
-- user-entered statement ending balances used to check reconciliations.

CREATE TABLE book_entry (
    id             INTEGER PRIMARY KEY,
    account_id     INTEGER NOT NULL REFERENCES account(id),
    entry_date     TEXT NOT NULL,
    amount_cents   INTEGER NOT NULL CHECK (amount_cents <> 0),
    description    TEXT NOT NULL,
    cleared_txn_id INTEGER UNIQUE REFERENCES bank_txn(id)
);
CREATE INDEX ix_book_entry_account_date ON book_entry(account_id, entry_date);

CREATE TABLE statement_balance (
    id                   INTEGER PRIMARY KEY,
    account_id           INTEGER NOT NULL REFERENCES account(id),
    month                TEXT NOT NULL,
    ending_balance_cents INTEGER NOT NULL,
    UNIQUE (account_id, month)
);

CREATE TRIGGER trg_book_entry_closed_ins BEFORE INSERT ON book_entry
WHEN EXISTS (SELECT 1 FROM period WHERE month = substr(NEW.entry_date, 1, 7) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_book_entry_closed_upd BEFORE UPDATE ON book_entry
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (substr(OLD.entry_date, 1, 7), substr(NEW.entry_date, 1, 7)) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_book_entry_closed_del BEFORE DELETE ON book_entry
WHEN EXISTS (SELECT 1 FROM period WHERE month = substr(OLD.entry_date, 1, 7) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;

CREATE TRIGGER trg_statement_balance_closed_upd BEFORE UPDATE ON statement_balance
WHEN EXISTS (SELECT 1 FROM period WHERE month IN (OLD.month, NEW.month) AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
CREATE TRIGGER trg_statement_balance_closed_ins BEFORE INSERT ON statement_balance
WHEN EXISTS (SELECT 1 FROM period WHERE month = NEW.month AND is_closed = 1)
BEGIN SELECT RAISE(ABORT, 'PERIOD_CLOSED'); END;
