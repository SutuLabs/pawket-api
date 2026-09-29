# Storage migration design and verification

Updated 2026-09-29. This is the single current migration design document. The frozen old implementation and its snapshots remain in `tests/baseline` as historical evidence, not as a specification for removed features. Work is confined to the isolated test environment; no production action is part of this plan.

## Objective and data sources

Remove the API's large PostgreSQL chain tables and Syncer dependency. Ordinary wallet coin records read the existing Chia full-node v2 SQLite database directly, through a read-only mount and read-only SQLite connection. This does not create a second chain database or copy the 279 GB Chia file. The code checks `database_version=2` and required `coin_record`/`hints` columns before querying. The Chia schema is an explicit dependency, as it was for the former Syncer; a Chia upgrade changing it requires adaptation and tests before use.

`/Wallet/get-puzzle`, `/Wallet/get-coin-solution`, and `/Wallet/get-block` use Chia RPC. `/Misc/prices` retains only a small local SQLite latest-value cache. `/Wallet/pushtx` keeps its Chia RPC behavior and writes diagnostic data to a local rotating JSONL log. `/Wallet/offers`, `/Wallet/network`, `/Misc/taildb`, and `/Misc/version` keep their existing external/config/file behavior, without PostgreSQL construction. All `/Name/*`, all `/Inscription/*`, and `/Wallet/analysis` are removed; their baseline snapshots are not migration gates. The old Syncer remains in the repository for reference but is not built or started by the API deployment script.

## Records contract

`POST /Wallet/records` preserves the old request fields `puzzleHash`, `hint`, `includeSpentCoins`, `startHeight`, `endHeight`, `pageStart`, and `pageLength`, and the response's `peekHeight`, `coins`, `balance`, and `balanceInfo` behavior where applicable. Height bounds apply to confirmation height (`startHeight` inclusive, `endHeight` exclusive); omitted bounds mean all history. Coin records, including spent state, are drawn from Chia's local indexes. `balance` and `balanceInfo` remain all-history values for the requested puzzle hash, not page or height-range totals. There is no coin processor call and no coin-classification/analysis cache.

The legacy fields `coinType` and `includeAnalysis` are deprecated. If either property is present, including `includeAnalysis:false`, return HTTP 200 with the normal outer shape and `coins: []`, silently. Do not classify, analyze, or return an error merely because these properties were supplied. This deliberately changes those legacy requests so the frontend can recognize that path as unavailable. Other request validation remains applicable. No `analysis` field is emitted for ordinary records.

`pageStart`/`pageLength` on the old route remain for compatibility, but offset pagination against a changing chain cannot promise a gap-free complete scan. A separate `POST /Wallet/coins` supplies cursor pagination. The earlier experimental `/Wallet/records-page` route is removed:

```json
{"puzzleHash":"0x...","hint":true,"includeSpentCoins":true,"startHeight":0,"endHeight":9000001,"pageLength":1000,"cursor":null}
```

`puzzleHash` is the 32-byte hash to search; `hint` selects `hints` rather than `puzzle_hash`. `pageLength` defaults to 100 and is limited to 1–1000. Omit optional height fields for no height restriction, and omit `cursor` on the first page. The response is `{"peekHeight":9341815,"coins":[{"puzzleHash":"0x...","records":[...]}],"nextCursor":"..."}`; `nextCursor` is absent after the last page. The new route has no `balance` or `balanceInfo` fields and does not calculate balances. The old `/Wallet/records` route retains those fields. Send back the same filter fields and the returned cursor until `nextCursor` is absent. The cursor fixes a peak-height/block-hash snapshot and the last `(sortHeight,coinId)` key. Coins confirmed after the snapshot are excluded; spends after it are treated as unspent. A cursor with changed filters is rejected with 400; a changed snapshot block hash is rejected with 409, requiring restart. Ordering is descending by effective max confirmation/spend height, then descending coin ID. This allows a frontend to fetch all ~8,000+ matching records without one huge response or repeating the earlier pages. The cursor is an opaque client token, not an authorization credential.

Both records routes query only Chia SQLite for coins. The cursor route uses keyset pagination; the legacy route still supports offset pagination. The large-hint test currently traverses more than 8,000 real coins at 1,000 per page, checking no duplicates and termination. SQL-only measurements on the test machine were about 0.1 s for either the first 100 rows or an offset near 8,000; endpoint latency additionally includes RPC peak lookup and serialization, so this is not an end-to-end latency guarantee.

## Other retained contracts

`get-puzzle` and `get-coin-solution` accept optional confirmation-height bounds, and fetch puzzle/solution by the coin's actual spent height. `get-coin-solution` retains both `coinId` and `coinIds` forms. `get-block` keeps the `blocks`/`refBlocks` shape, reference deduplication, ordering, and gzip/Base64 generator representation. Chia RPC errors and absent historical puzzle data are covered by targeted tests; the old limited PostgreSQL snapshot is not a complete live-chain equality oracle.

`/Misc/prices` immediately returns the last stored values and triggers a background refresh only when due. At most one external update runs per API instance; persisted cooldown/backoff prevents restart/request bounce. Failed or partial refreshes do not erase known currencies, and older timestamps do not overwrite newer values. Cold SQLite is seeded from [production-prices-2026-09-29.json](production-prices-2026-09-29.json), retaining the captured timestamps and XCH→CNY/USD/USDT rows, which must not be represented as current quotes. The old `coin_class_cache` table, if present in this API cache file, is dropped and space reclaimed on initialization; price rows remain. A working external price source remains a separately deferred task: Coinbase, CoinGecko, and CoinPaprika timed out from the test machine on 2026-09-29. Do not point this service at itself as a price upstream.

The push log is owner-restricted JSONL with configurable rotation (default 10 × 10 MiB); failure to log does not alter the push result. No PostgreSQL push-log table is used.

## Test baseline and current status

The frozen old API on test port 5057 and its PostgreSQL slice remain untouched. Its C# xUnit baseline previously passed 31/31 and includes real spent/unspent coins, positive puzzle/solution, block generator, and CAT/DID/NFT samples. See [README.md](README.md) for fixture details. That suite preserves the old contract, including now-deprecated class behavior; it is not expected to run unmodified against the new API.

The isolated replacement API runs on test port 5058 against the test machine's local Chia node, with its Chia directory bind-mounted read-only. The updated C# migration suite passed 43/43 after the records redesign and cache-cleanup test; rerun after each final change. It covers retained interfaces, deprecation-empty behavior for both legacy fields (including false), cursor traversal and filter rejection, height bounds, prices and one-update behavior, removed routes, and file logging. No live transaction or offer is submitted by automated tests. No complete-chain copy or 100,000-block Syncer replay is needed.

Run the test suite from `tests/migration/WalletBackend.MigrationTests.csproj` with `PAWKET_MIGRATION_URL` pointing to the isolated API. The API requires the full-node RPC host/port/certificate path, `ChiaDbPath` pointing to the mounted v2 SQLite DB, and a persistent small data directory for `prices.sqlite` and `pushtx.jsonl`. The frozen old baseline is separate and uses `PAWKET_BASELINE_URL`. Only test-environment deployment and test verification are in scope here.
