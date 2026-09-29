# Legacy API contract baseline

The API and Syncer images were built from Git commit
`b7b75acc424b8ffa84fa60bae432d0dbd12ba2c0` with submodule
`b59e4c3a37239d2a40dfb3f8f9808d9b431d0f8a`.

The baseline database is intentionally a limited mainnet slice. The Syncer is
stopped. It now has 15,200,006 coin records, 15,000,003 hints, 239,000 blocks,
and 6,642 parsed class rows. Do not resume full sync to run these tests.

The six configuration and validation cases are in `static-snapshot.json`; ten
real-data cases are in `data-snapshot.json`. Run the xUnit integration tests
against the frozen API and PostgreSQL database. From another machine, first
create an SSH tunnel to the API's loopback port:

```sh
ssh -N -L 15057:127.0.0.1:5057 1889u
```

Then run in another terminal:

```sh
PAWKET_BASELINE_URL=http://127.0.0.1:15057 \
  dotnet test tests/baseline/WalletBackend.BaselineTests.csproj
```

Keep the baseline PostgreSQL volume and API running while verifying. Responses containing
`peekHeight` or other chain state are exact only against this frozen database.
The stored `peekHeight` is the old Syncer's reported source height, not proof
that all records up to that height were imported. The current slice covers
positive spent and unspent coin results, pagination, coin details, and blocks
with generators. Plain hint, name, and inscription cases cover empty results;
the class-filtered records cases cover positive hinted coins.

The block endpoint gzip output has a changing header timestamp. The test
validates the gzip and compares the SHA-256 of its decompressed content;
all other response fields are compared exactly.

These cases do not exercise transaction submission or offer upload.

Five additional C# xUnit tests cover the currently ignored `records` height fields,
the 301-hash limit, a missing puzzle, the legacy single-coin solution shape, and an
unknown coin solution. A sixth test uses a newly parsed real spent coin to check
nonempty puzzle and solution values through both endpoints. Three more cover
unknown, duplicated, and empty-generator block heights. See
`STORAGE-MIGRATION-DESIGN.md` for migration scope and remaining focused cases.

Three additional positive `records` class cases (`CatV2`, `DidV1`, `NftV1`)
use narrowly seeded public-mainnet samples around height 9000000. They verify
classification and selected `analysis` fields. Positive hint, multiple puzzle
hashes, and multiple coin IDs are also covered. The total suite is 31 tests.
`SampleScanner` is a bounded, read-only C# discovery tool; `SampleSeeder` is a
separate, narrowly scoped test-database seeder for exactly those three samples.
Neither is part of the production API or a continuously running Syncer task.

The reviewed migration proposal is in [STORAGE-MIGRATION-DESIGN.md](STORAGE-MIGRATION-DESIGN.md).
The production `prices` response captured on 2026-09-29 is in
[production-prices-2026-09-29.json](production-prices-2026-09-29.json); it is a
contract fixture, not a live-price assertion.
