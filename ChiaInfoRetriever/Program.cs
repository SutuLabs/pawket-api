using ChiaApi;

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
var s = await client.GetCoinRecordByNameAsync("41858081f34c9859d5bad8941c40fd53158f4dfcb8a27dcbcfc4521c1790287b");// tx id
var p = await client.GetCoinRecordByNameAsync(s.CoinRecord.Coin.ParentCoinInfo);// tx id
var s2 = await client.GetPuzzleAndSolutionAsync (s.CoinRecord.Coin.ParentCoinInfo, s.CoinRecord.ConfirmedBlockIndex);

var d = s.Success;
