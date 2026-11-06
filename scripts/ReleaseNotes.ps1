# Reads releases/<version>.md for update.json. The GitHub release shows the whole file; the app shows one language.
# A file without markers is notes for every language. Otherwise each language starts with its own marker line:
#   <!-- lang: zh-CN -->
#   <!-- lang: en-US -->
# Markers are HTML comments, so they do not appear on the release page. Text before the first marker is refused,
# because it would belong to no language.
function Read-ReleaseNotes([Parameter(Mandatory)][string]$Path) {
    $text = (Get-Content -Raw -Encoding utf8 -LiteralPath $Path) -replace "`r`n", "`n"
    $pattern = '(?m)^[ \t]*<!--[ \t]*lang:[ \t]*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)[ \t]*-->[ \t]*$'
    $markers = [regex]::Matches($text, $pattern)
    if ($markers.Count -eq 0) { return $text.Trim() }
    if ($text.Substring(0, $markers[0].Index).Trim()) { throw "Text before the first language marker in $Path belongs to no language." }
    $notes = [ordered]@{}
    for ($i = 0; $i -lt $markers.Count; $i++) {
        $start = $markers[$i].Index + $markers[$i].Length
        $end = if ($i + 1 -lt $markers.Count) { $markers[$i + 1].Index } else { $text.Length }
        $language = $markers[$i].Groups[1].Value
        if ($notes.Contains($language)) { throw "Language $language appears twice in $Path." }
        $body = $text.Substring($start, $end - $start).Trim()
        if (!$body) { throw "Language $language has no notes in $Path." }
        $notes[$language] = $body
    }
    $notes
}
