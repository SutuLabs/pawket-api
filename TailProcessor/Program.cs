#nullable disable

using Ipfs.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

const string tailFile = "tail.json";
const string targetTailFile = "tails.json";
const string minifiedPngFolder = "logo_png";
const string minifiedJpgFolder = "logo_jpg";
const string srcImgFolder = "img";
const int width = 32;
const int height = 32;
const string IpfsApiUrl = "http://127.0.0.1:9051";
const string ipfsBaseUrl = "https://storage.pawket.app/ipfs/";

var ipfs = new IpfsClient() { ApiUri = new Uri(IpfsApiUrl) };

SourceTailEntity[] tails;
var tailFi = new FileInfo(tailFile);
if (tailFi.Exists && (DateTime.UtcNow - tailFi.LastWriteTimeUtc).TotalHours < 1)
{
    using var fs = new FileStream(tailFile, FileMode.Open);
    tails = await JsonSerializer.DeserializeAsync<SourceTailEntity[]>(fs);
}
else
{
    using var client = new HttpClient();
    tails = await client.GetFromJsonAsync<SourceTailEntity[]>("https://mainnet-api.taildatabase.com/tails");
    using var fs = new FileStream(tailFile, FileMode.OpenOrCreate);
    await JsonSerializer.SerializeAsync(fs, tails);
}

Console.WriteLine($"Total {tails.Length} tails");

if (!Directory.Exists(srcImgFolder)) Directory.CreateDirectory(srcImgFolder);
if (!Directory.Exists(minifiedJpgFolder)) Directory.CreateDirectory(minifiedJpgFolder);

var refTails = new TailEntity[] { };
if (File.Exists(targetTailFile))
{
    using var fs = new FileStream(targetTailFile, FileMode.Open);
    refTails = await JsonSerializer.DeserializeAsync<TailEntity[]>(fs);
}

var finalTails = new List<TailEntity>();
foreach (var tail in tails)
{
    var rt = refTails.FirstOrDefault(_ => _.launcher_id == tail.launcher_id);
    if (rt != null) continue;

    var srcImgFile = Path.Combine(srcImgFolder, tail.launcher_id + ".jpg");
    var minPngFile = Path.Combine(minifiedPngFolder, tail.launcher_id + ".png");
    var minJpgFile = Path.Combine(minifiedJpgFolder, tail.launcher_id + ".webp");

    var srcFi = new FileInfo(srcImgFile);
    if (!srcFi.Exists || srcFi.Length == 0)
    {
        Console.WriteLine($"Working on {tail.code}[{tail.name}][{tail.launcher_id}]:\n{tail.nft_uri}");
        var s = await DownloadWithRetryAsync(tail.nft_uri);
        using var fs = File.Create(srcImgFile);
        s.CopyTo(fs);
    }

    if (!File.Exists(minPngFile))
    {
        if (File.Exists(srcImgFile))
        {
            try
            {
                RefineImageFunctions.ResizeImage(srcImgFile, width, height, minPngFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to resize [{tail.launcher_id}], due to {ex.Message}");
            }
        }
    }

    //if (!File.Exists(minJpgFile))
    //{
    //    if (File.Exists(minPngFile))
    //    {
    //        try
    //        {
    //            RefineImageFunctions.SaveAsJpg(minPngFile, minJpgFile);
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.WriteLine($"Failed to resize [{tail.launcher_id}], due to {ex.Message}");
    //        }
    //    }
    //}

    if (File.Exists(minPngFile))
    {
        using var fs = File.OpenRead(minPngFile);
        Console.WriteLine($"Uploading {tail.code}.png");
        var link = await UploadToIpfs(fs, tail.code + ".png");
        finalTails.Add(new TailEntity(tail.name, tail.code, tail.description, tail.category, tail.launcher_id, ipfsBaseUrl + link));
    }
}

{
    using var fs = new FileStream(targetTailFile, FileMode.OpenOrCreate);
    await JsonSerializer.SerializeAsync(fs, finalTails);
}

async Task<string> UploadToIpfs(Stream stream, string filename)
{
    var ret = await ipfs.FileSystem.AddAsync(stream, filename, new Ipfs.CoreApi.AddFileOptions { Pin = true });
    var cid = ret.Id;

    var link = $"{cid}?filename={filename}";
    return link;
}

async Task<Stream> DownloadWithRetryAsync(string url, CancellationToken cancellationToken = default, uint? maxRetries = null, uint retryWait = 1000)
{
    var attempts = 0;
    var lastError = "";
    //var list = new[] {
    //    "ipfs.runfission.com",
    //    "jorropo.net",
    //    "ipfs.jpu.jp",
    //    "via0.com",
    //    "dweb.link",
    //    "ipfs.czip.it",
    //};
    //maxRetries = maxRetries ?? (uint)list.Length;
    maxRetries = maxRetries ?? 3u;
    var effUrl = url;

    try
    {
        while (attempts <= maxRetries)
        {
            //if (attempts > 0 && list.Length >= attempts)
            //{
            //    effUrl = url.Replace("nftstorage.link", list[attempts - 1]);
            //    Console.Write($"trying [{effUrl}]: ");
            //}

            try
            {
                using var client = GetClient(attempts > 0);
                client.Timeout = TimeSpan.FromSeconds(100);
                var s = await client.GetStreamAsync(effUrl);
                return s;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Console.WriteLine(ex.Message);
            }

            if (maxRetries == 0) break;

            attempts++;
            var waitTime = (int)retryWait * attempts;

            await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }
    catch (TaskCanceledException)
    {
        return null;
    }

    return null;
}

HttpClient GetClient(bool isProxy)
{
    var httpClientHandler = new HttpClientHandler
    {
        Proxy = new WebProxy { Address = new Uri($"http://10.177.0.122:8119"), },
    };

    return isProxy
        ? new HttpClient(handler: httpClientHandler, disposeHandler: true)
        : new HttpClient();
}

public record SourceTailEntity(
    string hash,
    string name,
    string code,
    string description,
    string category,
    string launcher_id,
    string eve_coin_id,
    string tail_reveal,
    string nft_uri);

public record TailEntity(
    string name,
    string code,
    string description,
    string category,
    string launcher_id,
    string uri);