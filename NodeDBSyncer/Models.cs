namespace NodeDBSyncer;

public record CoinRecord(
    long id,
    byte[] coin_name,
    long confirmed_index,
    long spent_index,
    bool coinbase,
    byte[] puzzle_hash,
    byte[] coin_parent,
    ulong amount,
    long timestamp);

/*
CREATE TABLE [dbo].[sync_coin_record](
	[id] [bigint] IDENTITY(1,1) NOT NULL,
	[coin_name] [binary](32) NOT NULL,
	[confirmed_index] [bigint] NOT NULL,
	[spent_index] [bigint] NOT NULL,
	[coinbase] [bit] NOT NULL,
	[puzzle_hash] [binary](32) NOT NULL,
	[coin_parent] [binary](32) NOT NULL,
	[amount] [bigint] NOT NULL,
	[timestamp] [bigint] NOT NULL
) ON [PRIMARY]


CREATE TABLE public.sync_coin_record
(
    id bigint NOT NULL,
    coin_name bytea NOT NULL,
    confirmed_index bigint NOT NULL,
    spent_index bigint NOT NULL,
    coinbase boolean NOT NULL,
    puzzle_hash bytea NOT NULL,
    coin_parent bytea NOT NULL,
    amount bigint NOT NULL,
    timestamp bigint NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.sync_coin_record
    OWNER to postgres;

CREATE INDEX IF NOT EXISTS idx_confirmed_index
    ON public.sync_coin_record USING btree
    (confirmed_index DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_spent_index
    ON public.sync_coin_record USING btree
    (spent_index DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_coin_parent
    ON public.sync_coin_record USING btree
    (coin_parent ASC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_puzzle_hash
    ON public.sync_coin_record USING btree
    (puzzle_hash ASC NULLS LAST)
	INCLUDE(amount, spent_index);

CREATE INDEX IF NOT EXISTS idx_coin_name
    ON public.sync_coin_record USING btree
    (coin_name ASC NULLS LAST);


select pg_table_size('idx_puzzle_hash'), pg_table_size('idx_coin_parent'), pg_table_size('idx_spent_index'), pg_table_size('idx_confirmed_index'), pg_table_size('idx_coin_name')
 */

public record HintRecord(
    long id,
    byte[] coin_id,
    byte[] hint);

/*
CREATE TABLE [dbo].[sync_hints](
	[id] [bigint] IDENTITY(1,1) NOT NULL,
	[coin_id] [binary](32) NOT NULL,
	[hint] [binary](32) NOT NULL
) ON [PRIMARY]

CREATE TABLE public.sync_hint_record
(
    id bigint NOT NULL,
    coin_id bytea NOT NULL,
    hint bytea NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.sync_hint_record
    OWNER to postgres;

CREATE INDEX IF NOT EXISTS idx_coin
    ON public.sync_hint_record USING btree
    (hint ASC NULLS LAST);

 */

public record SyncState(long spentHeight);

/*
CREATE TABLE public.sync_state
(
    id bigint NOT NULL,
    spent_height bigint NOT NULL,
    PRIMARY KEY (id)
);

ALTER TABLE IF EXISTS public.sync_state
    OWNER to postgres;

INSERT INTO public.sync_state (id, spent_height) VALUES (1, 1);
 */

public record SpentHeightChange(
    long id,
    long spent_height);
