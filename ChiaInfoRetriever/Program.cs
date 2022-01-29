using ChiaApi;
using ChiaApi.Models.Request.FullNode;

// command: redir :8666 :8555

var cfg = new ChiaApiConfig("../../../../private_full_node.crt", "../../../../private_full_node.key", "10.179.0.196", 8666);
var client = new FullNodeApiClient(cfg);
//var s = await client.GetBlockchainStateAsync();
//var s = await client.GetBlocksAsync(640000, 640003, false);
//var s = await client.GetBlockRecordByHeightAsync(0);
//var s = await client.GetAllMemPoolItemsAsync();
//var s = await client.GetCoinRecordByNameAsync("83cfec441ee3c7d152c54dcb1f5e700949c5842074ecaee388b85e64d3a2b85d");// tx id
//var s = await client.GetCoinRecordsByPuzzleHashAsync("0xa0b99203ac3cf4563ec0854096e52b8e015b2c436502cea065a1b840e9915f7b");//equal to address: xch15zueyqav8n69v0kqs4qfdeft3cq4ktzrv5pvagr95xuyp6v3taase36wyh
//var s = await client.GetCoinRecordsByParentIdsAsync(new[] { "0xa9e68a49f3038c38b28af15d43c71c23545fda1a87bbb6dc59af2d7e502100c3" });

//var s = await client.GetCoinRecordByNameAsync("41858081f34c9859d5bad8941c40fd53158f4dfcb8a27dcbcfc4521c1790287b");// tx id
//var p = await client.GetCoinRecordByNameAsync(s.CoinRecord.Coin.ParentCoinInfo);// tx id
//var s2 = await client.GetPuzzleAndSolutionAsync (s.CoinRecord.Coin.ParentCoinInfo, s.CoinRecord.ConfirmedBlockIndex);

//var ph = await client.GetCoinRecordsByPuzzleHashAsync("0x3eb239190ce59b4af1e461291b9185cea62d6072fd3718051a530fd8a8218bc0");//equal to address: xch186erjxgvukd54u0yvy53hyv9e6nz6crjl5m3spg62v8a32pp30qq2zkmtm
//var r = await client.GetCoinRecordByNameAsync("093798a8eac9965da0c4083735eae6aa7e9962247bb2865e46dd48e86b1982d7");// tx id

//var sbr = new SpendBundleRequest()
//{
//    SpendBundle = new SpendBundle
//    {
//        AggregatedSignature = "",
//        CoinSolutions = new List<ChiaApi.Models.Responses.Shared.CoinSpend>
//      {
//          new ChiaApi.Models.Responses.Shared.CoinSpend
//          {
//               Coin= new ChiaApi.Models.Responses.Shared.CoinItem{ Amount=1, ParentCoinInfo="", PuzzleHash=""},
//                PuzzleReveal="",
//                Solution="",
//          }
//      }
//    }
//};
//await client.PushTxAsync(sbr);

var s = await client.GetAllMemPoolItemsAsync();
var d = s.Success;
