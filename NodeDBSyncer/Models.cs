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
 */