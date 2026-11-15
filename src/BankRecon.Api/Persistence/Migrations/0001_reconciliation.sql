-- Imported files and statements are immutable facts: the application role may insert them, never change them.
CREATE TABLE recon.statement_file (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sha256      bytea       NOT NULL UNIQUE,
    format      text        NOT NULL,
    file_name   text,
    size_bytes  bigint      NOT NULL CHECK (size_bytes > 0),
    received_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE recon.statement (
    id              bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    file_id         bigint        NOT NULL REFERENCES recon.statement_file (id),
    statement_ref   text          NOT NULL,
    sequence_number text,
    account         text          NOT NULL,
    currency        char(3)       NOT NULL,
    opening_balance numeric(19,3) NOT NULL,
    opening_date    date          NOT NULL,
    closing_balance numeric(19,3),
    closing_date    date,
    entry_count     integer       NOT NULL DEFAULT 0,
    -- The same statement re-exported in a different file (other line endings, other bank tool) is still a duplicate.
    CONSTRAINT statement_identity UNIQUE NULLS NOT DISTINCT (account, statement_ref, sequence_number)
);

-- One row per matchable movement: a whole entry (detail_index -1) or one itemised detail of a batch booking.
CREATE TABLE recon.bank_line (
    id                bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    statement_id      bigint        NOT NULL REFERENCES recon.statement (id),
    entry_index       integer       NOT NULL,
    detail_index      integer       NOT NULL DEFAULT -1,
    account           text          NOT NULL,
    currency          char(3)       NOT NULL,
    amount            numeric(19,3) NOT NULL,
    value_date        date          NOT NULL,
    booking_date      date,
    is_reversal       boolean       NOT NULL,
    transaction_type  text          NOT NULL,
    customer_reference text,
    bank_reference    text,
    end_to_end_id     text,
    remittance        text,
    narrative         text,
    counterparty_name text,
    counterparty_iban text,
    status            text          NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'matched', 'written_off')),
    UNIQUE (statement_id, entry_index, detail_index)
);

CREATE INDEX bank_line_open ON recon.bank_line (account, value_date) WHERE status = 'open';

CREATE TABLE recon.ledger_item (
    id                bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account           text          NOT NULL,
    external_id       text          NOT NULL,
    currency          char(3)       NOT NULL,
    amount            numeric(19,3) NOT NULL,
    expected_date     date          NOT NULL,
    reference         text,
    counterparty_name text,
    status            text          NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'matched', 'written_off')),
    created_at        timestamptz   NOT NULL DEFAULT now(),
    UNIQUE (account, external_id)
);

CREATE INDEX ledger_item_open ON recon.ledger_item (account, expected_date) WHERE status = 'open';

CREATE TABLE recon.recon_run (
    id             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account        text        NOT NULL,
    started_at     timestamptz NOT NULL DEFAULT now(),
    finished_at    timestamptz,
    options        jsonb       NOT NULL,
    bank_lines     integer     NOT NULL DEFAULT 0,
    ledger_items   integer     NOT NULL DEFAULT 0,
    matches        integer     NOT NULL DEFAULT 0,
    queued         integer     NOT NULL DEFAULT 0,
    auto_resolved  integer     NOT NULL DEFAULT 0
);

CREATE TABLE recon.match (
    id                bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id            bigint        REFERENCES recon.recon_run (id),
    account           text          NOT NULL,
    rule              text          NOT NULL,
    score             numeric(6,4)  NOT NULL,
    amount_difference numeric(19,3) NOT NULL,
    explanation       text          NOT NULL,
    decided_by        text          NOT NULL,
    created_at        timestamptz   NOT NULL DEFAULT now()
);

-- The UNIQUE columns are the real guarantee: a bank line or ledger item belongs to at most one match, whatever
-- races the application code loses.
CREATE TABLE recon.match_bank_line (
    match_id     bigint NOT NULL REFERENCES recon.match (id),
    bank_line_id bigint NOT NULL UNIQUE REFERENCES recon.bank_line (id),
    PRIMARY KEY (match_id, bank_line_id)
);

CREATE TABLE recon.match_ledger_item (
    match_id       bigint NOT NULL REFERENCES recon.match (id),
    ledger_item_id bigint NOT NULL UNIQUE REFERENCES recon.ledger_item (id),
    PRIMARY KEY (match_id, ledger_item_id)
);

-- Work queue for what the matcher could not decide. Claiming uses a lease (claimed_by + lease_expires_at) and a
-- fencing token (lease_token, incremented on every claim): a worker whose lease expired and was re-claimed can no
-- longer resolve the item, because its token is stale.
CREATE TABLE recon.work_item (
    id               bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account          text          NOT NULL,
    subject_kind     text          NOT NULL CHECK (subject_kind IN ('bank_line', 'ledger_item')),
    subject_id       bigint        NOT NULL,
    reason           text          NOT NULL,
    candidates       jsonb         NOT NULL DEFAULT '[]',
    priority         numeric(19,3) NOT NULL,
    status           text          NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'claimed', 'resolved')),
    claimed_by       text,
    lease_expires_at timestamptz,
    lease_token      bigint        NOT NULL DEFAULT 0,
    attempts         integer       NOT NULL DEFAULT 0,
    resolution       text,
    resolution_note  text,
    resolved_by      text,
    resolved_at      timestamptz,
    created_at       timestamptz   NOT NULL DEFAULT now(),
    updated_at       timestamptz   NOT NULL DEFAULT now(),
    CHECK ((status = 'claimed') = (claimed_by IS NOT NULL AND lease_expires_at IS NOT NULL))
);

-- At most one unresolved work item per subject; reconciliation runs upsert against it.
CREATE UNIQUE INDEX work_item_one_active ON recon.work_item (subject_kind, subject_id) WHERE status <> 'resolved';

-- The claim query scans this index in priority order and skips locked rows.
CREATE INDEX work_item_ready ON recon.work_item (priority DESC, id) WHERE status <> 'resolved';

-- Least privilege on top of the schema defaults (SELECT, INSERT, UPDATE; never DELETE).
REVOKE UPDATE ON recon.statement_file, recon.match, recon.match_bank_line, recon.match_ledger_item FROM recon_app;
REVOKE UPDATE ON recon.statement FROM recon_app;
GRANT UPDATE (closing_balance, closing_date, entry_count) ON recon.statement TO recon_app;
