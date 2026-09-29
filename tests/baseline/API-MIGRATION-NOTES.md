# API baseline and storage migration notes

Recorded 2026-09-29. This is a planning note, not an API change. Preserve current responses until a separately approved deprecation or migration is implemented.

## Out of scope

- All `/Name/*` and `/Inscription/*` routes are planned for removal. They are still implemented; this note does not remove them.
- `POST /Wallet/analysis` is also planned for removal and is not a migration target.
- `/Wallet/pushtx`, `/Wallet/offers`, and `/Wallet/network` are not part of the database-read migration.
- `/Misc/prices` will continue using PostgreSQL.

The existing empty-result baseline cases for `/Name/recent` and `/Inscription/ticks` describe current behavior, but they are not migration gates for retained APIs.

## Important baseline gaps

`POST /Wallet/records` has seven snapshot cases: invalid coin type, spent records, spent filtering, two unspent pages, unmatched hint, and unknown puzzle hash. There is no positive hint or coin-class case, analysis payload, multiple puzzle hashes, or complete-chain balance check. The limited database slice makes current balance and `peekHeight` snapshots unsuitable as proof of complete-mainnet behavior.

`POST /Wallet/get-coin-solution` has two snapshot cases plus legacy-shape, unknown-ID, and real nonempty puzzle/solution tests. Multiple IDs, unspent coins, and paging still lack cases.

Initially, read-only verification on the baseline test database found 10,919,769 spent `sync_coin_record` rows but zero `sync_coin_class` rows. The empty reveal/solution in the old snapshot results from the API's left join to this missing parsed-data table; it is not evidence that a spent coin has no reveal on-chain. A one-off Syncer container then parsed existing blocks only, with coin/block synchronization and other parse tasks disabled, and was stopped and removed after producing 6,639 nonempty class rows. A new xUnit case checks a real spent coin through both `get-puzzle` and `get-coin-solution`, including the exact nonempty puzzle and solution.

The three primary routes do not currently support caller-selected block-height filtering. `records` accepts `startHeight` and `endHeight`, but neither affects its SQL result (`startHeight` is passed as an unused SQL parameter). `get-puzzle` and `get-coin-solution` do not accept a height field. The SQL for all three applies the internal `sync_state` upper bound; this is not a request filter.

For the new implementation, implement effective start/end height filtering for `records`, and add optional start/end height fields to `get-puzzle` and `get-coin-solution`. Requests without the new fields must remain valid. This is an intentional behavior change for requests that supply height fields, so the old ignored-height test describes legacy behavior only, not the future contract.

Confirmed height contract: `startHeight` inclusive and `endHeight` exclusive, both interpreted as **coin confirmation height**, matching the bundled Chia full-node coin-record RPC. Omitted bounds mean no restriction. For `get-puzzle` and `get-coin-solution`, first look up coin records within the interval by ID, then retrieve puzzle/solution using each coin's actual spent height. An optional `spentStartHeight`/`spentEndHeight` pair can be added later only if callers need filtering by spend height. Balance fields in `records` retain their current all-history meaning rather than becoming range totals.

## Frontend usage to confirm before migration

The ordinary `records` puzzle-hash lookup can be supplied by Chia full-node RPC. The potentially unsupported features are within the same route: `coinType` (`CatV2`, `DidV1`, `NftV1`) filters hinted coins by parsed parent puzzle modules; `includeAnalysis: true` can return custom `analysis` JSON for these class-filtered results. The full-node coin-record RPC does not directly return this classification or analysis. Preserve them only with on-demand parsing or a smaller derived store. Ask frontend owners whether they send either option, read `coins[].records[].analysis`, or rely on class-filtered `balance`/`balanceInfo` being omitted. Also check `hint: true` usage, especially combined with `coinType`: current code gives `coinType` priority over `hint`. Ordinary hint lookup itself is supported by full-node RPC, but lacks a positive baseline case.

Frontend confirmation: `coinType` and `includeAnalysis` must remain supported. `hint` combinations and class-query balance omission are not migration blockers.

## Proposed implementation for retained classification and analysis

1. Start with on-demand computation. Obtain candidate child coins via Chia's hint RPC, then look up each parent coin's spend and pass its puzzle/solution and coin metadata to the existing local coin processor (`/analyze_tx`). Match the current `mods` strings to `CatV2`, `DidV1`, or `NftV1`; return its `analysis` JSON only when requested. Apply class filtering before the existing ordering and pagination. Add a bounded short-lived cache keyed by parent coin ID and chain state to avoid repeated parsing. Validate results against positive CAT/DID/NFT examples and measure latency and RPC load, especially for large hint result sets.
2. If the measured request-time work is too expensive, retain only a small derived index: parent coin ID, class/mod identifier, analysis JSON where needed, and reorg/height bookkeeping. Continue obtaining raw coin, hint, puzzle, solution, and block data from Chia. A lean indexer would scan new Chia spends and backfill historical class entries; it must not require `sync_block`, `sync_coin_record`, or full `sync_coin_class` storage. This is an implementation change, not achievable by merely setting the current Syncer batch sizes to zero.

The current Syncer parses from `sync_block`, and its separate analysis pass joins `sync_coin_class` to `sync_coin_record`. Disabling coin/block synchronization while leaving those jobs enabled can parse an existing bounded slice, as demonstrated in the test environment, but cannot maintain complete current data indefinitely.

For positive CAT/DID/NFT baseline cases, the existing Syncer could not jump from its stored height 239000 to 9000000 without first syncing the intervening blocks. A bounded, read-only C# scanner instead queried Chia and the existing coin processor over [9000000, 9000700), locating all three types by height 9000688; there was no need to scan or store 100000 blocks. The exact child hints were verified against Chia's own hint index. A narrow test-only seeder inserted three parent coins, three child coins, three hint rows, and three parsed class rows into the isolated baseline PostgreSQL database. All prior 25 xUnit tests still passed. New xUnit cases assert one real positive `records` class and `analysis` response for each type. The production database and core server code were not changed.

The additional xUnit tests lock in the ignored height fields, the 301-hash validation, the missing-puzzle error, the legacy single-spend shape, and the unknown coin's empty-array response. Block edge cases now cover an unknown height, a duplicate height, and a block with an empty generator.

## API business data sources (inventory, not migration scope)

| Route | Current source |
| --- | --- |
| `POST /Wallet/records` | PostgreSQL coin, hint, and class tables |
| `POST /Wallet/get-puzzle` | PostgreSQL coin and class tables |
| `POST /Wallet/get-coin-solution` | PostgreSQL coin and class tables |
| `POST /Wallet/get-block` | PostgreSQL block table |
| `POST /Wallet/analysis` | PostgreSQL parsed coin analysis; planned for removal |
| `POST /Name/resolve` | PostgreSQL-derived CNS index, cached |
| `POST /Name/wealthiest` | PostgreSQL-derived CNS index and balances, cached |
| `GET /Name/all` | PostgreSQL-derived CNS index, cached |
| `GET /Inscription/holder/{address}` | PostgreSQL-derived inscription index, cached |
| `GET /Misc/prices` | PostgreSQL price series if `PriceSourceUrl` is blank; otherwise external HTTP |
| `POST /Wallet/pushtx` | Chia full-node RPC; writes a PostgreSQL push log |
| `POST /Wallet/offers` | External offer-upload HTTP service |
| `GET /Wallet/network` | Application configuration |
| `GET /Misc/taildb` | Local `tails.json` |
| `GET /Misc/version` | Assembly metadata |

No retained read endpoint currently obtains its business data directly from the Chia full-node RPC. The controller constructs an RPC client, but its puzzle/coin-solution RPC helpers are unused by the live routes.

`DataAccess` opens PostgreSQL in its constructor. `WalletController` and `MiscController` inject it, so even routes whose business data comes from configuration, files, Chia, or an external service currently have an incidental PostgreSQL availability dependency. `WalletController` also injects `PushLogHelper`, which opens PostgreSQL in its constructor. Removing PostgreSQL requires decoupling this initialization and deciding what to do with push logs and stored prices.

Coin and block records are candidates for full-node RPC reads, but exact compatibility requires matching the current filters, ordering, pagination, balance aggregation, peak-height semantics, missing-record behavior, and block generator/reference formatting. The current baseline is insufficient to prove that. Name, inscription, and analysis routes are planned for removal rather than migration. Prices remain in PostgreSQL; offer upload remains an external-service call.
