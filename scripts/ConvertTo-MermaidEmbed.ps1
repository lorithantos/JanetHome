<#
.SYNOPSIS
    Converts Mermaid diagram source to an inline HTML <img> tag using mermaid.ink.
.DESCRIPTION
    Takes Mermaid source (from parameter, file, or clipboard), base64-encodes it,
    and produces an <img src="https://mermaid.ink/svg/base64:..."> tag that renders
    inline in ADO wiki, GitHub, and most markdown viewers.

    Can also process an entire markdown file, replacing fenced ```mermaid blocks
    with rendered <img> tags.
.PARAMETER Text
    Mermaid diagram source as a string.
.PARAMETER File
    Path to a .mmd file containing Mermaid source.
.PARAMETER Clipboard
    Read Mermaid source from clipboard.
.PARAMETER MarkdownFile
    Path to a markdown file. All ```mermaid fenced blocks will be replaced with
    <img> tags. Outputs to -OutFile or stdout.
.PARAMETER OutFile
    Write output to a file instead of stdout.
.PARAMETER Alt
    Alt text for the <img> tag. Default: "diagram".
.PARAMETER KeepSource
    When processing markdown, keep the original fenced block as an HTML comment
    above the <img> tag so the source is recoverable.
.EXAMPLE
    ConvertTo-MermaidEmbed.ps1 -Text "graph LR; A-->B"
.EXAMPLE
    ConvertTo-MermaidEmbed.ps1 -File diagram.mmd -Alt "Architecture"
.EXAMPLE
    ConvertTo-MermaidEmbed.ps1 -MarkdownFile report.md -OutFile report-shared.md -KeepSource
.EXAMPLE
    ConvertTo-MermaidEmbed.ps1 -Clipboard | Set-Clipboard
#>
[CmdletBinding(DefaultParameterSetName = 'Text')]
param(
    [Parameter(ParameterSetName = 'Text', Position = 0, Mandatory)]
    [string]$Text,

    [Parameter(ParameterSetName = 'File', Mandatory)]
    [string]$File,

    [Parameter(ParameterSetName = 'Clipboard', Mandatory)]
    [switch]$Clipboard,

    [Parameter(ParameterSetName = 'Markdown', Mandatory)]
    [string]$MarkdownFile,

    [string]$OutFile,
    [string]$Alt = 'diagram',
    [switch]$KeepSource,
    [switch]$Link
)

function ConvertTo-ImgTag {
    param([string]$MermaidSource, [string]$AltText)
    $trimmed = $MermaidSource.Trim()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($trimmed)
    $b64 = [Convert]::ToBase64String($bytes)
    $url = "https://mermaid.ink/svg/$b64`?bgColor=transparent"
    return "<img src=`"$url`" alt=`"$AltText`" />"
}

function ConvertTo-LiveLink {
    param([string]$MermaidSource, [string]$AltText)
    Add-Type -AssemblyName System.IO.Compression
    $trimmed = $MermaidSource.Trim()
    $jsonCode = $trimmed.Replace('\','\\').Replace('"','\"').Replace("`r",'').Replace("`n",'\n')
    $json = '{"code":"' + $jsonCode + '","mermaid":{"theme":"default"},"autoSync":true}'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $ms = [System.IO.MemoryStream]::new()
    $ds = [System.IO.Compression.DeflateStream]::new($ms, [System.IO.Compression.CompressionLevel]::Optimal)
    $ds.Write($bytes, 0, $bytes.Length)
    $ds.Close()
    $compressed = $ms.ToArray()
    $b64 = [Convert]::ToBase64String($compressed)
    $b64url = $b64.Replace('+','-').Replace('/','_').TrimEnd('=')
    return "https://mermaid.live/edit#pako:$b64url"
}

switch ($PSCmdlet.ParameterSetName) {
    'Text' {
        if ($Link) { $result = ConvertTo-LiveLink -MermaidSource $Text -AltText $Alt } else { $result = ConvertTo-ImgTag -MermaidSource $Text -AltText $Alt }
    }
    'File' {
        $source = Get-Content -Path $File -Raw -Encoding UTF8
        if ($Link) { $result = ConvertTo-LiveLink -MermaidSource $source -AltText $Alt } else { $result = ConvertTo-ImgTag -MermaidSource $source -AltText $Alt }
    }
    'Clipboard' {
        $source = Get-Clipboard -Raw
        if ($Link) { $result = ConvertTo-LiveLink -MermaidSource $source -AltText $Alt } else { $result = ConvertTo-ImgTag -MermaidSource $source -AltText $Alt }
    }
    'Markdown' {
        $md = Get-Content -Path $MarkdownFile -Raw -Encoding UTF8
        $pattern = '(?ms)```mermaid\s*\r?\n(.*?)```'
        $count = 0
        $result = [regex]::Replace($md, $pattern, {
            param($m)
            $count++
            $source = $m.Groups[1].Value
            if ($Link) { $tag = ConvertTo-LiveLink -MermaidSource $source -AltText "$Alt $count" } else { $tag = ConvertTo-ImgTag -MermaidSource $source -AltText "$Alt $count" }
            if ($KeepSource) {
                "<!-- mermaid source`n$($source.Trim())`n-->`n$tag"
            } else {
                $tag
            }
        })
        Write-Host "Replaced $count mermaid block(s)"
    }
}

if ($OutFile) {
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($OutFile, $result, $utf8NoBom)
    Write-Host "Wrote $OutFile"
} else {
    $result
}