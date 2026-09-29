module GitKay.Tests.LinkTests

open Xunit
open Swensen.Unquote
open GitKay.Core

let private urls text =
    Links.find text |> List.map (fun s -> text.Substring(s.Start, s.Length))

[<Fact>]
let ``a bitbucket pull request hint yields the whole url`` () =
    test <@ urls "remote:   https://bitbucket.org/acme/repo/pull-requests/7?t=1" = [ "https://bitbucket.org/acme/repo/pull-requests/7?t=1" ] @>

[<Fact>]
let ``trailing sentence punctuation and unbalanced brackets are left out`` () =
    test <@ urls "see https://x.io/a." = [ "https://x.io/a" ] @>
    test <@ urls "(see https://x.io/a)." = [ "https://x.io/a" ] @>
    test <@ urls "https://en.wikipedia.org/wiki/Foo_(bar)" = [ "https://en.wikipedia.org/wiki/Foo_(bar)" ] @>
    test <@ urls "<http://x.io/a>, and \"https://y.io\"" = [ "http://x.io/a"; "https://y.io" ] @>

[<Fact>]
let ``several urls, none, and bare schemes`` () =
    test <@ urls "a http://a.io b https://b.io/c" = [ "http://a.io"; "https://b.io/c" ] @>
    test <@ urls "" = [] && urls "no links, ftp://x.io https:// http://" = [] @>
    test <@ urls "xhttp://nope.io" = [] @>

[<Fact>]
let ``linkAt finds the url under an index`` () =
    let text = "go https://x.io/a now"
    test <@ Links.linkAt text 3 = Some "https://x.io/a" && Links.linkAt text 2 = None && Links.linkAt text 17 = None @>
