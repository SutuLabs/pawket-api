//using ChiaApi;
//using ChiaApi.Models.Request.FullNode;

//// command: redir :8666 :8555

//var cfg = new ChiaApiConfig("../../../../private_full_node.crt", "../../../../private_full_node.key", "10.179.0.196", 8666);
//var client = new FullNodeApiClient(cfg);
////var s = await client.GetBlockchainStateAsync();
//var s = await client.GetBlocksAsync(640000, 641003, false);
////var s = await client.GetBlocksAsync(532397, 532398, false);
////var s = await client.GetBlockRecordByHeightAsync(0);
////var s = await client.GetAllMemPoolItemsAsync();
////var s = await client.GetCoinRecordByNameAsync("83cfec441ee3c7d152c54dcb1f5e700949c5842074ecaee388b85e64d3a2b85d");// tx id
////var s = await client.GetCoinRecordsByPuzzleHashAsync("0xa0b99203ac3cf4563ec0854096e52b8e015b2c436502cea065a1b840e9915f7b");//equal to address: xch15zueyqav8n69v0kqs4qfdeft3cq4ktzrv5pvagr95xuyp6v3taase36wyh
////var s = await client.GetCoinRecordsByParentIdsAsync(new[] { "0xa9e68a49f3038c38b28af15d43c71c23545fda1a87bbb6dc59af2d7e502100c3" });

////var s = await client.GetCoinRecordByNameAsync("41858081f34c9859d5bad8941c40fd53158f4dfcb8a27dcbcfc4521c1790287b");// tx id
////var p = await client.GetCoinRecordByNameAsync(s.CoinRecord.Coin.ParentCoinInfo);// tx id
////var s2 = await client.GetPuzzleAndSolutionAsync (s.CoinRecord.Coin.ParentCoinInfo, s.CoinRecord.ConfirmedBlockIndex);

////var ph = await client.GetCoinRecordsByPuzzleHashAsync("0x3eb239190ce59b4af1e461291b9185cea62d6072fd3718051a530fd8a8218bc0");//equal to address: xch186erjxgvukd54u0yvy53hyv9e6nz6crjl5m3spg62v8a32pp30qq2zkmtm
////var r = await client.GetCoinRecordByNameAsync("093798a8eac9965da0c4083735eae6aa7e9962247bb2865e46dd48e86b1982d7");// tx id

////var sbr = new SpendBundleRequest()
////{
////    SpendBundle = new SpendBundle
////    {
////        AggregatedSignature = "",
////        CoinSolutions = new List<ChiaApi.Models.Responses.Shared.CoinSpend>
////      {
////          new ChiaApi.Models.Responses.Shared.CoinSpend
////          {
////               Coin= new ChiaApi.Models.Responses.Shared.CoinItem{ Amount=1, ParentCoinInfo="", PuzzleHash=""},
////                PuzzleReveal="",
////                Solution="",
////          }
////      }
////    }
////};
////await client.PushTxAsync(sbr);

////var s = await client.GetAllMemPoolItemsAsync();
////var d = s.Success;

////var arr = new[] { "33894aef41710b78929de83c09ba712cdb051e64d903eb8aa01ecc706f9947b6", "498e3afade695b4c1ce9a51923d5a37c2c79e25db2be28a9bed80467370da042", "c326e631f763c2a0e5b512e0251fa1c70f4e67af21e4d5c87b269562112a24ae", "dc4b8b189ff0019c71929f92ccb0ff4c7d45b4abd62c8f4b36c34a0da115ae03", "6a379c0f845de13a2440f60eb7ec23c403e2094bccbd5f6ad0b1d846cf76bb5a", "e3dd22247a196aab16e3d4aa12459ef4027f9200ba7375d4de0fed8aa9e05566", "31fd2be4684896be9ae5c3498544ff60e7cb68ce2f672bf47f66915f445f0cda", "123317e2d94a32e4fba4531fe8fa156106e352715c86ce5351117538b2270247", "26a4334c845f075db94852a80ab6cc27cce75c54292ed609fc6cc3392c0bc5d1", "85fbd803a93b04832d9cf7e932852129460618e4da0a2fb99fcf446986c0710c", "fc88251c3b347329445592cf196000a78a191392a6240fe3625007107b5c3ca8", "1da1e8a9eaebe1ea9d259b30db8a7e664185b5495db535035495a06f3633e000", "e4f08172431b28cadd163d70a8cc3d6d7042137486b5bcb4f6786298524322b9", "40a6b3cd7eacbf5928e2ca15294c6c1ddf7a8e070f41351264d66a4579e3dc2e", "8d0878db4bf471f3e51b7b3e40e2cc88cb6477ae18f7bdc13540305962ac3482", "15c8a7bc662247fe0dc847408b57447c842a4203d0da87ccd8de4d1f596e1524", "35e9e4f17851b1ee87dcca979a956860f4fab7edebd263afa7fab1c026034bc9", "c6d448364843672c60e9aeb7e3e590f83412a3dd4a6ea109517deb811b0cddd2", "fc3331835084e8838acacde339c020f5ac7e5a690ea576db5842ddb07946cc91", "38fec25931c372730c00caaf7fd511813432cd240cd324a8f7aa8e632bd56abb", "c7885f8591c67e3312eec0fb1fb33afd5b507c1173bcb8c1e22e02c9cdd68588", "c251a2f24f8e52a50d89ddb56c620a0b4665e5dbf6ac4564d503eafd80d32f40", "e2f861be1c9df4d31a76c152025f2557eb689052589e5531934a5a5a8c7cf333", "d708ab800e9d0589897f3590cfac89feea3226c2e73aceb91ff70ea0af682cee", "e25493070b3c3e34404f2e564771bbc625970b92497f84301e83d116d83b1e0f", "4ccfdc752b8742548904e9c9f0d51b8e635125dfca99be02def341784c665c82", "5f0365c4ea939cca35f76bedac90e42f59d9dbd68e6b8a0e0074df5c0d6d1682", "03ad9664d2ada581680ddd5dd8d487b545491e002d069c2d819de8e83d3108a1", "67126804ef57b72d880660fa63f059d309f6d6a88a1fbbc98609ca2f89d66ce0", "6d3d83747520873da5f5b1b6dd62728add9603564a93273d6c4771b4debffd8c", "0573d0dc3d49121ffb3d04dd468dcd4568a419856681ea4662113c17227c62fc", "825aef6d7c8a9dc8e5fb8a69df8dd8850e8179d9265cf8043cfa148acf5491f6", "fcfbad93fa644e1e0ab406ff8ddbe6d0338f4670a48d68e24993486de34a322e", "38ac430467ea698bae2796195ce9da742d75878a9e6dbfae150e3696be9231a9", "c8ca7dbbba6a3cd78c7e4028a04d390463c5c9c9541fef507b6b304542730b8e", "38f945428e5f65d0f3cd57b96165a2cf3bf30c18da89df38b6ecac60364163d2", "0475e3fe63716dcb90b9a4a87c97cd0a7b5c29c5bbed2dfc7c9134ba491f73ab", "b122e57f9984c9bb56af68865245a9b6976a402866300e488f1594a87c6dc7dc", "4be8d5e8997bfa22090950491a03f8b2636e724018ee53b4272366166f10acc8", "0301c2e3a8b1ec39bb0d47efe120f045162541e4b9f8786a27df2f002915994f", "f32f0d45a89ddc3026b83ca3a79c87dfcd248584359967315d72f977c1460634", "7558b2a84ec822c20b64707d6cfd25199ed0fca82f0a23eb6aec26c5a99cf509", "aa812c7881b4838a9678ba2c931b82cb971f82ae118e802323f1c5349b7aa876", "dde210565b07f4ac1eef584690363db09c3e2a1d35386aedfa7590db1f1e52bc", "6f6610e7f4ee3d9fce1c85df30588dcc0e76f9b8aee6f71db59daea736c4d00f", "6ed0bb3278eb6f67fda712eb19e6ca80aab2c206d90c5ee553ab0206b2666cfe", "20131d23b11d062502327ad209a2cc2d033868a3cb9ff359c814b40b35ac2664", "a30cd35f29947137811ac26435abc235180c2c11fa9bab02247685660c42e987", "b5c5b9038ef9e315eaa4830933e0c2273fbd0b4cecf3b46365f1883d19bd5cce", "057c2c68bd7accc8d9c2bb0406b25b7be0bc3caeb4991407187d3e9187507a14", "ff7cc0057456a4e406ce404caa86561fc4efa53278e5218f8ec3f2385b2a5420", "4a36bab3a5ffa91e719dc4c7f07dc2cf95ebfc8c8240bde632019d7baf312986", "ed56f26215a43458968ad74b1d58a5b43ce321a7554f6892c4fabd4de23c7715", "3d521ddf2d8495f3b512c1ea694d6e15febb79fc28eec397bc8893ba9b5f7098", "4f56b11c1eda5f70031df8c90ef3bc94e88e3999a36d52c7b0a3c56fe6ce5f34", "e24ca40efe72438aa0222fbf32071e1634c8fd872a914e40cc38026908e197a2", "e4e037c31681e5b6742e513b374f5705e18a120e56a48c62029f59a9116c40a7", "11f5319793baa904c93d54918a2c28cbdbc8d35389a58e4fdb853803d989dc38", "862826f1466769cd13da13161ea8a12cf6e9118e5b89f0ad70f48bf7b5ae4f96", "d91f0d1b4c0e40387f6d5a7fe329ad862ee1d44899b11eac0e47565b952c9d37", "a845f0a1b715c24ef50c041ae781833db1df7871b752e36690967647378cc381", "54d9314a66425a8c56d3e5a62eb68f0b29c3bc9a8c59cf5e8ca5bf658298c12c", "8d32057b18eaca253fc01873ea852630a101192f2c7c869f85965eb0be6d08c7", "64a20778f400c05a0dcac0f54270b347dc9cd1bf388dcc25e73320c06cd5b860", "ec540dd595996b2e6dc20f78db94eb9c0aa54989778e19275ba279f457e9cd4a", "e6a3d507825a019128346d86862daa8ea977714d295d41d77c8e474f9c2e073d", "c48089310f264d19c38a8ffaee1863194b2058dec20b46d4d9cc84193d659b8b", "e394ae9193a5cff957bd87f9e537466ad563fa1e61c416da5d39cb44f2426306", "b1e028d6b38e2c9df2df7355e6353be30c2166bc909e30292dc10ad1d9376b5d", "70da159e5c80ccd8e1a546d06bf4ab65f81d27a1139611c820151b77bbb7dc0d", "19920b2f9d2d8e09ef982f63280a8748dc8542401448c6b090403fa3e2e15f94", "ca7613ab442747fe1be624cc76fffa2baef6b561c2af09ca67c033044294a921", "5e2f27e9f5cb06532d093cc2c003303aba4bbe878023c2db93b102eec5ac93bc", "35dd9ede490b9eec41c51c1a3e1aca46c38dc93c8f864401b21366cbd26b5e9f", "62b87ba07f879cc24f4b332353dcbea7fb93029d730680ce7a3b055b0b2def24", "7084f2cff083cdd3f4d4e1a3a2f893966e1cff0bce08afe0eaaec023f810c545", "1df89fdccf628318d4f5b971ff0c484585463b491a7c94ca85717f4c8142b4cb", "45323343c3fa88ffeb88e7fa259ba98a2ca60b36c27eec7237ff1f63df8ca773", "de1d3d933cdcba028e3711fd698ee76400994d06e8ebe15720496b28754f27d1", "e54b98cdad1456b092ea26dacfd7f6d344ad1a7ec35e17d02667d93fc6dddd09", "d3cc6a66133ed20c42b21b3d9f281819c869295bc8ce065f37ec9f4bff6d031e", "13f84e6ca12a98ef48f03b390113fdb85144f52a560f38cd10c427a52a71420c", "21db32e45685e75444d0b527a85f5b3c960094c9a93233e22e5a5b181b629545", "fde2b647d88d746da7a6ce4bd9a8764e51ee815e116e6c683a478b7081c5c0cb", "6439a0e2f481ed8732117da4066244bc406c37af16eb3b1ce6549b9f02419603", "8c35977fffc8b99dd3b546ba89af4988b666b5294a28a3c2a1063d5a0f04b66e", "dd7b367867cc6eb735693d5d817f2b603fedccd98599aa0a90411e83e039e8c1", "6616d4f93febf56ab88b57c70c99f84b20b74764df87dbe2fcdb3ada77991e70", "703cb303adf92a1959227dea32eb15168b09b53009ba112ce73695b84e485acc", "8a596cc824532bf26f4b846308932eddbb72d112100c418b472fdb90247d9707", "7d1a5777ab394452464080686e5be619aa3a0a6edd14d7b27061442f739e6b08", "dfd1af191cb94f21cedb0942c6adf2cf77028e3bc7bfe4fd539e8a9796d6e5f7", "97db5b57a36932782ec16e31e9d4a1c491906d7ded495a2d1e5d656118282fcc", "3fe2af17c501a8a74540107f12dbccf0557917dcbaea93c41e4a5aff87fdb445", "91785ba242ab109a40cfbbfeb50231c6f671641580a02235c9017714cf18dac0", "52cf14b2c85773b9054056c962d4a39f1cd72c98bc62eda8f95be671627b2c28", "1100d073b830064d7f18d5b0e7a9b0c2a7f39c300683c3b197a5dec4530ab5c6", "a1f792245d3b243d22b391b18d4671f5420a2dac17920c2909a72ff593de680b", "0ae671bcc485c5d15fc5fc0378e3f0e374ecfdc07ea32505f401d65004b0d43e", "7e32b44b949522aa623c81d15a60758f173438958adbc5f275e957e4a6d849a2" };
////arr = arr.Select(_ => $"0x{_}").ToArray();

////var records = (await client.GetCoinRecordsByNamesAsync(arr, null, null, true)).CoinRecords;

////var d = records.Where(_ => _.Spent).ToArray();

//var c = s.Blocks.Where(_ => _.TransactionsGeneratorRefList?.Count > 0);
////var c = s.Blocks.Where(_ => _.TransactionsGenerator.Length == 0).ToArray();
////var d1 = c[2];
////var d2 = c[3];
//var f = c.First();
//var ar = await client.GetAdditionsAndRemovalsAsync(f.HeaderHash);
//var dddd = string.Join(",", c.Select(_ => _.TransactionsInfo.RewardClaimsIncorporated.Count));
//var ddd = c;

using chia.dotnet;

var endpoint = new EndpointInfo
{
    //CertPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../key/testnet/private_daemon.crt"),
    //KeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../key/testnet/private_daemon.key"),
    //Uri = new Uri("wss://10.179.0.197:55466/"),
    //CertPath = "../../../../key/testnet/private_full_node.crt",
    //KeyPath = "../../../../key/testnet/private_full_node.key",
    //Uri = new Uri("https://10.179.0.197:8566/"),
    CertPath = "../../../../private_full_node.crt",
    KeyPath = "../../../../private_full_node.key",
    Uri = new Uri("https://10.179.0.196:8666/"),
};

var endpoint2 = Config.Open().GetEndpoint("daemon");

using var rpcClient = new HttpRpcClient(endpoint);
//using var rpcClient = new WebSocketRpcClient(endpoint);
//await rpcClient.Connect();

//var daemon = new DaemonProxy(rpcClient, "client");
//await daemon.RegisterService();

var fullNode = new FullNodeProxy(rpcClient, "client");
var state = await fullNode.GetBlockchainState();
Console.WriteLine($"This node is synced: {state.Sync.Synced}");
var info = await fullNode.GetNetworkInfo();
Console.WriteLine($"This node : {info.NetworkPrefix}");
var b = await fullNode.GetBlocks(225697, 225699, false);
var s = await fullNode.GetBlocks(640000, 641003, false);
var c = s.Where(_ => _.TransactionsGeneratorRefList?.Count > 0);
//var c = s.Blocks.Where(_ => _.TransactionsGenerator.Length == 0).ToArray();
//var d1 = c[2];
//var d2 = c[3];
var f = c.First();
var ar = await fullNode.GetAdditionsAndRemovals(f.HeaderHash);
var dddd = string.Join(",", c.Select(_ => _.TransactionsInfo.RewardClaimsIncorporated.Count));
var ddd = c;

//var records = await fullNode.GetCoinRecordsByHint("0eb720d9195ffe59684b62b12d54791be7ad3bb6207f5eb92e0e1b40ecbc1155", true);
//Console.WriteLine($"This node : {info.NetworkPrefix}");


