open System
open System.Linq
open System.IO
open System.Net
open System.Net.Http
open System.Threading

open HtmlAgilityPack
open Newtonsoft.Json

let isNotNull x = (isNull >> not) x

let getCars path =
    path
    |> File.ReadAllLines
    |> Seq.skip 1
    |> Seq.map (fun line ->
        let splitArr = line.Split(',')
        splitArr.[0].Trim(), splitArr.[1].Trim())
    |> Seq.filter (fun (_, car) -> String.IsNullOrEmpty(car) |> not)
    |> Seq.map (fun (fullName, car) -> fullName, WebUtility.UrlEncode(car))


let getSearchUrls cars =
    let urlTemplate: Printf.StringFormat<(string -> string)> = "http://www.chinacar.com.cn/sousuo.html?keys=%s"
    cars
    |> Seq.map (fun (fullName, car) -> (fullName, car, (sprintf urlTemplate car)))

let client = new HttpClient()

let tryGetString (url: string) =
    use request = new HttpRequestMessage(HttpMethod.Get, url)
    use response = client.SendAsync(request).Result
    if response.IsSuccessStatusCode then
        let responseString = response.Content.ReadAsStringAsync().Result
        Some responseString
    else
        None

let getCarPageUrls (searchUrls: seq<string * string * string>) =
    let doc = new HtmlDocument()
    seq {
        for fullName, car, searchUrl in searchUrls do
            let responseStringOpt = tryGetString searchUrl
            if responseStringOpt.IsSome then
                let responseString = responseStringOpt.Value
                doc.LoadHtml(responseString)
                let resultNodes = doc.DocumentNode.SelectNodes("//div[@class='pro_title']/a")
                if (isNotNull resultNodes) && resultNodes.Count > 0 then
                    let urlSeg = resultNodes.[0].Attributes.["href"].Value
                    if urlSeg.StartsWith("http") then
                        yield fullName, car, urlSeg
                    else
                        let carPageUrl = "http://www.chinacar.com.cn" + urlSeg
                        yield fullName, car, carPageUrl
    }

type CarResult = {
    FullName: string
    Name: string
    Url: string
    ImageUrl: string
    ImagePath: string
    Price: string
    LaunchTime: string
}

let getCarResults (carPageUrls: seq<string * string * string>) =
    Directory.CreateDirectory("img") |> ignore
    let imgPathTemplate: Printf.StringFormat<(string -> string)> = "img/%s"

    let rand = Random()

    let randomSleep () =
        let interval = rand.Next(300, 1500)
        Thread.Sleep(interval)

    let downloadImage (imagePath: string) (imageUrl: string) =
        try
            let responseBytes = client.GetByteArrayAsync(imageUrl).Result
            File.WriteAllBytes(imagePath, responseBytes)
        with
        | _ -> File.Create(imagePath) |> ignore

    let trySingleOrDefault (nodes: HtmlNodeCollection) =
        if isNotNull nodes then
            nodes.SingleOrDefault()
        else
            null

    let tryGetSingleNode (doc: HtmlDocument) (xpaths: string[]) =
        let result =
            xpaths
            |> Seq.map (doc.DocumentNode.SelectNodes >> trySingleOrDefault)
            |> Seq.skipWhile isNull
            |> Seq.tryPick (fun node -> if isNotNull node then Some node else None)
        match result with
        | Some node -> node
        | _ -> null

    seq {
        for fullName, car, carPageUrl in carPageUrls do
            randomSleep()

            let responseString = client.GetStringAsync(carPageUrl).Result
            let doc = HtmlDocument()
            doc.LoadHtml(responseString)

            let imageUrlPaths =
                [|
                    "//div[@id='preview']/div[@class='jqzoom']/img"
                |]

            let pricePaths =
                [|
                    "//div[@class='affiche_base_product_baojias']/table/tr[2]/td[2]"
                    "//div[@class='Bus_final_page_text_1']/ul/li[1]/b"
                |]

            let launchTimePaths =
                [|
                    "//div[@class='parameter_box_s']/table/tr[4]/td[4]"
                    "//*[@id='table']/table[1]/tr[4]/td[4]"
                    "//*[@id='p_dhArrCon_1']/table/tr[4]/td[4]"
                |]
            
            let imageUrlNode = tryGetSingleNode doc imageUrlPaths
            let priceNode = tryGetSingleNode doc pricePaths
            let launchTimeNode = tryGetSingleNode doc launchTimePaths

            let name = car
            let url = carPageUrl
            let imageUrl = if isNotNull imageUrlNode then imageUrlNode.Attributes.["src"].Value else ""
            let imagePath = 
                if imageUrl <> "" then
                    let imageUri = Uri(imageUrl)
                    let imageFileName = imageUri.Segments.Last()
                    sprintf imgPathTemplate imageFileName
                else
                    ""

            if imagePath <> "" then downloadImage imagePath imageUrl

            let price = if isNotNull priceNode then priceNode.InnerText else ""
            let launchTime = if isNotNull launchTimeNode then launchTimeNode.InnerText else ""
                
            let result = {
                FullName = fullName
                Name = name
                Url = url
                ImageUrl = imageUrl
                ImagePath = imagePath
                Price = price
                LaunchTime = launchTime
            }

            printfn "%A" result

            yield result
    }

[<EntryPoint>]
let main argv =
    getCars @"cars.csv"
    |> getSearchUrls
    |> getCarPageUrls
    |> getCarResults
    |> JsonConvert.SerializeObject
    |> fun json -> File.WriteAllText("data.json", json)

    client.Dispose()

    0
