-- The server side of the sync engine: an append-only change log per store with a dense sequence, the merged state
-- of every entity, and the devices allowed to sync. See docs/adr/0003 for why the store row is the sequencer.

CREATE TABLE sync.stores (
    store_id   text        PRIMARY KEY CHECK (length(store_id) BETWEEN 1 AND 64),
    -- Last sequence number handed out in this store. A push locks this row (FOR UPDATE) for its whole transaction,
    -- so within a store sequence order equals commit order and a pull cursor can never skip a late-committing push.
    head       bigint      NOT NULL DEFAULT 0 CHECK (head >= 0),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE sync.devices (
    node_id      text        PRIMARY KEY CHECK (length(node_id) BETWEEN 1 AND 64),
    store_id     text        NOT NULL REFERENCES sync.stores (store_id),
    -- Only the SHA-256 of the bearer token is stored: a database dump does not hand out device credentials.
    token_sha256 bytea       NOT NULL UNIQUE CHECK (length(token_sha256) = 32),
    created_at   timestamptz NOT NULL DEFAULT now(),
    revoked_at   timestamptz
);

CREATE TABLE sync.changes (
    store_id    text        NOT NULL REFERENCES sync.stores (store_id),
    seq         bigint      NOT NULL CHECK (seq > 0),
    change_id   uuid        NOT NULL,
    entity      text        NOT NULL,
    entity_id   uuid        NOT NULL,
    hlc         bigint      NOT NULL CHECK (hlc >= 0),
    node_id     text        NOT NULL,
    ops         jsonb       NOT NULL CHECK (jsonb_typeof(ops) = 'array'),
    -- Hash of the canonical change body: a replay with the same id must carry the same body.
    body_sha256 bytea       NOT NULL CHECK (length(body_sha256) = 32),
    received_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, seq),
    -- The idempotency arbiter: a change id is stored at most once, whichever device or retry sends it.
    CONSTRAINT changes_change_id_key UNIQUE (change_id)
);

CREATE TABLE sync.entities (
    store_id    text   NOT NULL REFERENCES sync.stores (store_id),
    entity      text   NOT NULL,
    entity_id   uuid   NOT NULL,
    state       jsonb  NOT NULL,
    updated_seq bigint NOT NULL,
    PRIMARY KEY (store_id, entity, entity_id)
);

-- The change log is history: no UPDATE or DELETE, not even by the schema owner by accident.
CREATE FUNCTION sync.forbid_change_log_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'sync.changes is append-only' USING ERRCODE = '23001';
END;
$$;

CREATE TRIGGER changes_append_only
    BEFORE UPDATE OR DELETE ON sync.changes
    FOR EACH ROW EXECUTE FUNCTION sync.forbid_change_log_mutation();

-- Narrow the default DML grants: the application appends to the log and never deletes anything.
REVOKE UPDATE, DELETE ON sync.changes FROM sync_app;
REVOKE DELETE ON sync.entities, sync.stores, sync.devices FROM sync_app;
