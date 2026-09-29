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

`POST /Wallet/get-coin-solution` has two snapshot cases plus legacy-shape and unknown-ID tests. The spent coin's stored puzzle and solution are empty, so the central nonempty `puzzle_reveal`/`solution` response is not yet protected. Multiple IDs, unspent coins, and paging also lack cases.

The three primary routes do not currently support caller-selected block-height filtering. `records` accepts `startHeight` and `endHeight`, but neither affects its SQL result (`startHeight` is passed as an unused SQL parameter). `get-puzzle` and `get-coin-solution` do not accept a height field. The SQL for all three applies the internal `sync_state` upper bound; this is not a request filter.

For the new implementation, implement effective start/end height filtering for `records`, and add optional start/end height fields to `get-puzzle` and `get-coin-solution`. Requests without the new fields must remain valid. This is an intentional behavior change for requests that supply height fields, so the old ignored-height test describes legacy behavior only, not the future contract.

The additional xUnit tests lock in the ignored height fields, the 301-hash validation, the missing-puzzle error, the legacy single-spend shape, and the unknown coin's empty-array response. All 21 tests passed on the existing test-machine API and PostgreSQL slice.

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
