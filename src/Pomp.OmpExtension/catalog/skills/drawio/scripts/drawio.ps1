<#
.SYNOPSIS
  Reads, checks and diffs draw.io (.drawio) files for the drawio skill. Windows PowerShell 5.1 or later; nothing to install.

.DESCRIPTION
  read      <file>                   The diagram as JSON: pages and their cells (id, type, pomp* link attributes, label as
                                     plain text, parent/source/target with their labels, relation style keys, geometry).
  check     <file>                   Structural problems (duplicate ids, edges or children pointing at missing cells,
                                     copied links, unknown pompKind). Exit code 1 when there are errors.
  snapshot  <file>                   Saves the current state as the baseline the next diff compares with
                                     (<workspace>/.pomp/local/drawio/<relative path>.json).
  diff      <file> [-Baseline <path>] What changed since the baseline: added, removed and changed linked content per page.
                                     Geometry and colour-only edits are only counted (cosmetic). -Baseline may name a
                                     snapshot .json or another .drawio file (for example one taken from git).
  decode    <file> [-Write]          The file with every page as plain XML (draw.io may save pages compressed); -Write
                                     rewrites the file that way so it can be edited as text. draw.io opens both forms.

  The workspace is -Workspace, else the nearest folder above the file that holds .pomp, .git or a .sln file.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('read', 'check', 'snapshot', 'diff', 'decode')][string]$Command,
    [Parameter(Mandatory = $true, Position = 1)][string]$Path,
    [string]$Workspace,
    [string]$Baseline,
    [switch]$Write
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

# Style keys that carry meaning in the skill's notation (relation kinds, shapes); everything else is cosmetic.
$SemanticStyleKeys = @('shape', 'endArrow', 'startArrow', 'endFill', 'startFill', 'dashed')
$KnownKinds = @('project', 'module', 'namespace', 'folder', 'file', 'class', 'interface', 'struct', 'record', 'enum', 'member',
    'table', 'column', 'state', 'step', 'decision', 'start', 'end', 'participant', 'message', 'note', 'component',
    'inherits', 'implements', 'references', 'depends', 'calls', 'transition', 'relation', 'flow', 'contains')

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 2 }

function Resolve-File([string]$file) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Fail "File not found: $file" }
    return (Resolve-Path -LiteralPath $file).ProviderPath
}

function Find-Workspace([string]$file) {
    if ($Workspace) { return (Resolve-Path -LiteralPath $Workspace).ProviderPath.TrimEnd('\', '/') }
    $dir = Split-Path -Parent $file
    while ($dir) {
        if ((Test-Path -LiteralPath (Join-Path $dir '.pomp')) -or (Test-Path -LiteralPath (Join-Path $dir '.git')) -or
            @(Get-ChildItem -LiteralPath $dir -Filter '*.sln*' -File -ErrorAction SilentlyContinue).Count -gt 0) { return $dir.TrimEnd('\', '/') }
        $parent = Split-Path -Parent $dir
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    return (Split-Path -Parent $file)
}

function Get-Relative([string]$root, [string]$file) {
    $prefix = $root.TrimEnd('\', '/') + '\'
    if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { Fail "The file is outside the workspace $root" }
    return $file.Substring($prefix.Length).Replace('\', '/')
}

function Get-BaselinePath([string]$file) {
    $root = Find-Workspace $file
    $relative = Get-Relative $root $file
    return (Join-Path (Join-Path $root '.pomp\local\drawio') ($relative.Replace('/', '\') + '.json'))
}

function Read-Xml([string]$text) {
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader($text)), $settings)
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.PreserveWhitespace = $false
        $doc.Load($reader)
        return $doc
    }
    finally { $reader.Dispose() }
}

# draw.io's compressed page: base64 of raw deflate of encodeURIComponent(xml).
function Expand-Page([string]$data) {
    $bytes = [Convert]::FromBase64String($data.Trim())
    $compressedStream = New-Object System.IO.MemoryStream(, $bytes)
    $deflate = New-Object System.IO.Compression.DeflateStream($compressedStream, [System.IO.Compression.CompressionMode]::Decompress)
    $reader = New-Object System.IO.StreamReader($deflate, [System.Text.Encoding]::UTF8)
    try { $encoded = $reader.ReadToEnd() } finally { $reader.Dispose() }
    return [Uri]::UnescapeDataString($encoded)
}

# Pages as @{ id; name; model (mxGraphModel element); compressed }.
function Get-Pages([System.Xml.XmlDocument]$doc) {
    $pages = New-Object System.Collections.ArrayList
    $root = $doc.DocumentElement
    if ($root.LocalName -eq 'mxGraphModel') {
        [void]$pages.Add(@{ id = 'page-1'; name = 'Page-1'; model = $root; compressed = $false })
        return , $pages
    }
    if ($root.LocalName -ne 'mxfile') { Fail "Not a draw.io file: the root element is <$($root.LocalName)>." }
    $index = 0
    foreach ($diagram in @($root.SelectNodes('diagram'))) {
        $index++
        $id = if ($diagram.HasAttribute('id')) { $diagram.GetAttribute('id') } else { "page-$index" }
        $name = if ($diagram.HasAttribute('name')) { $diagram.GetAttribute('name') } else { "Page-$index" }
        $model = $diagram.SelectSingleNode('mxGraphModel')
        $compressed = $false
        if ($null -eq $model) {
            $text = $diagram.InnerText
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $inner = Read-Xml (Expand-Page $text)
            $model = $inner.DocumentElement
            $compressed = $true
        }
        [void]$pages.Add(@{ id = $id; name = $name; model = $model; compressed = $compressed })
    }
    return , $pages
}

function Get-PlainText([string]$value, [string]$style) {
    if ([string]::IsNullOrEmpty($value)) { return '' }
    $text = $value
    # html=1 labels are HTML (what draw.io writes for its own shapes); others are plain text, so "Task<Receipt>" stays intact.
    if ($style -match '(^|;)html=1(;|$)') {
        $text = [regex]::Replace($text, '(?i)<br\s*/?>|</(div|p|li|tr)>', "`n")
        $text = [regex]::Replace($text, '<[^>]+>', '')
        $text = [System.Net.WebUtility]::HtmlDecode($text)
    }
    $lines = $text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
    return ($lines -join "`n").Replace([char]0xA0, ' ')
}

function Get-StyleParts([string]$style) {
    $names = New-Object System.Collections.ArrayList
    $keys = [ordered]@{}
    foreach ($part in ($style -split ';')) {
        if (-not $part) { continue }
        $eq = $part.IndexOf('=')
        if ($eq -lt 0) { [void]$names.Add($part); continue }
        $key = $part.Substring(0, $eq)
        if ($SemanticStyleKeys -contains $key) { $keys[$key] = $part.Substring($eq + 1) }
    }
    return @{ names = [object[]]$names.ToArray(); keys = $keys }
}

# draw.io ids (and pompRef symbols) are case-sensitive, but PowerShell's @{} and [ordered]@{} ignore case: a field `name` and a
# property `Name` would collide. Every map keyed by an id or a ref uses ordinal comparison.
function New-IdMap { return New-Object System.Collections.Hashtable ([StringComparer]::Ordinal) }
function New-OrderedIdMap { return New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal) }

# Cells of one page: the mxCell elements, directly or wrapped in <object>/<UserObject> that carries label and custom attributes.
function Get-Cells($page) {
    $cells = New-OrderedIdMap
    $rootNode = $page.model.SelectSingleNode('root')
    if ($null -eq $rootNode) { return $cells }
    foreach ($node in @($rootNode.ChildNodes)) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $wrapper = $null; $cell = $node
        if ($node.LocalName -eq 'object' -or $node.LocalName -eq 'UserObject') { $wrapper = $node; $cell = $node.SelectSingleNode('mxCell') }
        elseif ($node.LocalName -ne 'mxCell') { continue }
        if ($null -eq $cell) { continue }
        $id = if ($wrapper) { $wrapper.GetAttribute('id') } else { $cell.GetAttribute('id') }
        $style = $cell.GetAttribute('style')
        $raw = if ($wrapper) { $wrapper.GetAttribute('label') } else { $cell.GetAttribute('value') }
        $type = if ($cell.GetAttribute('edge') -eq '1') { 'edge' } elseif ($cell.GetAttribute('vertex') -eq '1') { 'vertex' } else { 'layer' }
        $pomp = [ordered]@{}; $attrs = [ordered]@{}
        if ($wrapper) {
            foreach ($a in $wrapper.Attributes) {
                if ($a.Name -in @('id', 'label', 'placeholders')) { continue }
                if ($a.Name.StartsWith('pomp')) { $pomp[$a.Name] = $a.Value } else { $attrs[$a.Name] = $a.Value }
            }
        }
        $geometry = $null
        $g = $cell.SelectSingleNode('mxGeometry')
        if ($g -and $type -eq 'vertex') {
            $geometry = [ordered]@{ x = $g.GetAttribute('x'); y = $g.GetAttribute('y'); width = $g.GetAttribute('width'); height = $g.GetAttribute('height') }
        }
        $parts = Get-StyleParts $style
        $entry = [ordered]@{
            id = $id; type = $type
            pompKind = $(if ($pomp.Contains('pompKind')) { $pomp['pompKind'] } else { $null })
            pompRef = $(if ($pomp.Contains('pompRef')) { $pomp['pompRef'] } else { $null })
            label = (Get-PlainText $raw $style)
            parent = $cell.GetAttribute('parent'); source = $cell.GetAttribute('source'); target = $cell.GetAttribute('target')
            styleNames = $parts.names; styleKeys = $parts.keys
            pomp = $pomp; attributes = $attrs; geometry = $geometry
            style = $style
        }
        if ($cells.Contains($id)) { $entry['duplicateId'] = $true; $id = $id + '#dup' + $cells.Count }
        $cells[$id] = $entry
    }
    # Readable context for the model: the labels of parent/source/target.
    foreach ($entry in $cells.Values) {
        foreach ($field in @('parent', 'source', 'target')) {
            $ref = $entry[$field]
            if ($ref -and $cells.Contains($ref)) { $entry[$field + 'Label'] = $cells[$ref].label }
        }
    }
    return $cells
}

function Read-Model([string]$file) {
    $text = [System.IO.File]::ReadAllText($file)
    $doc = Read-Xml $text
    $pages = New-Object System.Collections.ArrayList
    foreach ($page in (Get-Pages $doc)) {
        $cells = Get-Cells $page
        [void]$pages.Add([ordered]@{ id = $page.id; name = $page.name; compressed = $page.compressed; cells = [object[]]@($cells.Values) })
    }
    return [ordered]@{ file = $file; pages = [object[]]$pages.ToArray() }
}

function Write-Json($value) { Write-Output (ConvertTo-Json -InputObject $value -Depth 12 -Compress:$false) }

function Get-Problems($model) {
    $errors = New-Object System.Collections.ArrayList
    $warnings = New-Object System.Collections.ArrayList
    foreach ($page in $model.pages) {
        $ids = New-IdMap
        foreach ($c in $page.cells) { $ids[[string]$c.id] = $c }
        $refs = New-IdMap
        foreach ($c in $page.cells) {
            $where = "page '$($page.name)', cell '$($c.id)'"
            if ($c.Contains('duplicateId')) { [void]$errors.Add("$where`: duplicate id") }
            if ($c.parent -and -not $ids.ContainsKey($c.parent)) { [void]$errors.Add("$where`: parent '$($c.parent)' does not exist") }
            if ($c.type -eq 'edge') {
                foreach ($end in @('source', 'target')) {
                    # A loose end is a normal draw.io state (a line not yet attached); an id that does not exist is a broken file.
                    if (-not $c[$end]) { [void]$warnings.Add("$where`: edge has no connected $end") }
                    elseif (-not $ids.ContainsKey($c[$end])) { [void]$errors.Add("$where`: $end '$($c[$end])' does not exist") }
                }
            }
            if ($c.pompKind -and $KnownKinds -notcontains $c.pompKind) { [void]$warnings.Add("$where`: unknown pompKind '$($c.pompKind)'") }
            if ($c.pompRef) {
                if ($c.pompRef -match '^[a-zA-Z]:|^[\\/]|\\') { [void]$errors.Add("$where`: pompRef must be workspace-relative with forward slashes") }
                if ($c.type -eq 'vertex') {
                    if ($refs.ContainsKey($c.pompRef)) { [void]$warnings.Add("$where`: pompRef '$($c.pompRef)' is also on '$($refs[$c.pompRef])' (a copied shape?)") }
                    else { $refs[$c.pompRef] = $c.id }
                }
            }
        }
    }
    return @{ errors = [object[]]$errors.ToArray(); warnings = [object[]]$warnings.ToArray() }
}

function ConvertTo-Hashtable($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $h = [ordered]@{}
        foreach ($p in $value.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hashtable $p.Value }
        return $h
    }
    # The leading comma keeps an empty or one-item array an array when it leaves the function.
    if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) { return , [object[]]@($value | ForEach-Object { ConvertTo-Hashtable $_ }) }
    return $value
}

function Read-Baseline([string]$file) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    if ($file.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
        return ConvertTo-Hashtable ([System.IO.File]::ReadAllText($file) | ConvertFrom-Json)
    }
    return Read-Model $file
}

function Compare-Value($a, $b) {
    $ja = ConvertTo-Json -InputObject $a -Depth 6 -Compress
    $jb = ConvertTo-Json -InputObject $b -Depth 6 -Compress
    return $ja -eq $jb
}

function Get-Size($value) {
    if ($null -eq $value) { return 0 }
    if ($value -is [System.Collections.IDictionary]) { return $value.Count }
    return @($value).Count
}

function Get-Brief($c) {
    $brief = [ordered]@{ id = $c.id; type = $c.type; pompKind = $c.pompKind; pompRef = $c.pompRef; label = $c.label }
    foreach ($field in @('parent', 'source', 'target')) {
        if ($c[$field]) { $brief[$field] = $c[$field]; if ($c.Contains($field + 'Label')) { $brief[$field + 'Label'] = $c[$field + 'Label'] } }
    }
    if ((Get-Size $c.pomp) -gt 0) { $brief['pomp'] = $c.pomp }
    if ((Get-Size $c.attributes) -gt 0) { $brief['attributes'] = $c.attributes }
    if ((Get-Size $c.styleNames) -gt 0) { $brief['styleNames'] = $c.styleNames }
    if ((Get-Size $c.styleKeys) -gt 0) { $brief['styleKeys'] = $c.styleKeys }
    return $brief
}

function Compare-Models($old, $new) {
    $result = New-Object System.Collections.ArrayList
    $oldPages = New-IdMap
    foreach ($p in $old.pages) { $oldPages[[string]$p.id] = $p }
    $seen = New-IdMap
    foreach ($page in $new.pages) {
        $before = $null
        if ($oldPages.ContainsKey([string]$page.id)) { $before = $oldPages[[string]$page.id] }
        else { foreach ($p in $old.pages) { if ($p.name -eq $page.name -and -not $seen.ContainsKey([string]$p.id)) { $before = $p; break } } }
        if ($null -eq $before) {
            [void]$result.Add([ordered]@{ id = $page.id; name = $page.name; status = 'added'; added = [object[]]@($page.cells | Where-Object { $_.type -ne 'layer' } | ForEach-Object { Get-Brief $_ }) })
            continue
        }
        $seen[[string]$before.id] = $true
        $oldCells = New-IdMap
        foreach ($c in $before.cells) { $oldCells[[string]$c.id] = $c }
        $added = New-Object System.Collections.ArrayList
        $changed = New-Object System.Collections.ArrayList
        $cosmetic = 0
        $newIds = New-IdMap
        foreach ($c in $page.cells) {
            $newIds[[string]$c.id] = $true
            if (-not $oldCells.ContainsKey([string]$c.id)) { if ($c.type -ne 'layer') { [void]$added.Add((Get-Brief $c)) }; continue }
            $o = $oldCells[[string]$c.id]
            $fields = [ordered]@{}
            foreach ($f in @('label', 'type', 'parent', 'source', 'target')) {
                if ([string]$o[$f] -ne [string]$c[$f]) {
                    $change = [ordered]@{ from = $o[$f]; to = $c[$f] }
                    if ($f -in @('parent', 'source', 'target')) {
                        if ($o.Contains($f + 'Label')) { $change['fromLabel'] = $o[$f + 'Label'] }
                        if ($c.Contains($f + 'Label')) { $change['toLabel'] = $c[$f + 'Label'] }
                    }
                    $fields[$f] = $change
                }
            }
            foreach ($f in @('pomp', 'attributes', 'styleKeys', 'styleNames')) {
                if (-not (Compare-Value $o[$f] $c[$f])) { $fields[$f] = [ordered]@{ from = $o[$f]; to = $c[$f] } }
            }
            if ($fields.Count -gt 0) {
                $entry = Get-Brief $c
                $entry['changes'] = $fields
                [void]$changed.Add($entry)
            }
            elseif (-not (Compare-Value $o.geometry $c.geometry) -or $o.style -ne $c.style) { $cosmetic++ }
        }
        $removed = [object[]]@($before.cells | Where-Object { -not $newIds.ContainsKey([string]$_.id) -and $_.type -ne 'layer' } | ForEach-Object { Get-Brief $_ })
        $status = if ($added.Count + $changed.Count + $removed.Count -gt 0) { 'changed' } elseif ($cosmetic -gt 0 -or $page.name -ne $before.name) { 'cosmetic' } else { 'same' }
        $entry = [ordered]@{ id = $page.id; name = $page.name; status = $status; added = [object[]]$added.ToArray(); removed = $removed; changed = [object[]]$changed.ToArray(); cosmetic = $cosmetic }
        if ($page.name -ne $before.name) { $entry['renamedFrom'] = $before.name }
        [void]$result.Add($entry)
    }
    foreach ($p in $old.pages) {
        if (-not $seen.ContainsKey([string]$p.id)) {
            [void]$result.Add([ordered]@{ id = $p.id; name = $p.name; status = 'removed'; removed = [object[]]@($p.cells | Where-Object { $_.type -ne 'layer' } | ForEach-Object { Get-Brief $_ }) })
        }
    }
    return , [object[]]$result.ToArray()
}

$file = Resolve-File $Path
switch ($Command) {
    'read' {
        Write-Json (Read-Model $file)
    }
    'check' {
        $problems = Get-Problems (Read-Model $file)
        Write-Json ([ordered]@{ file = $file; ok = ($problems.errors.Count -eq 0); errors = $problems.errors; warnings = $problems.warnings })
        if ($problems.errors.Count -gt 0) { exit 1 }
    }
    'snapshot' {
        $model = Read-Model $file
        $problems = Get-Problems $model
        if ($problems.errors.Count -gt 0) { Write-Json ([ordered]@{ file = $file; ok = $false; errors = $problems.errors }); exit 1 }
        $target = Get-BaselinePath $file
        [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target))
        $model['savedUtc'] = [DateTime]::UtcNow.ToString('o')
        $model['sha256'] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        [System.IO.File]::WriteAllText($target, (ConvertTo-Json -InputObject $model -Depth 12 -Compress), (New-Object System.Text.UTF8Encoding($false)))
        Write-Json ([ordered]@{ file = $file; baseline = $target; pages = $model.pages.Count; warnings = $problems.warnings })
    }
    'diff' {
        $source = if ($Baseline) { (Resolve-Path -LiteralPath $Baseline).ProviderPath } else { Get-BaselinePath $file }
        $old = Read-Baseline $source
        if ($null -eq $old) { Write-Json ([ordered]@{ file = $file; baseline = $source; baselineFound = $false }); exit 3 }
        $new = Read-Model $file
        $problems = Get-Problems $new
        $same = $old.Contains('sha256') -and $old.sha256 -eq (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        Write-Json ([ordered]@{ file = $file; baseline = $source; baselineFound = $true; baselineSavedUtc = $(if ($old.Contains('savedUtc')) { $old.savedUtc } else { $null })
            unchangedFile = $same; pages = (Compare-Models $old $new); errors = $problems.errors; warnings = $problems.warnings })
    }
    'decode' {
        $doc = Read-Xml ([System.IO.File]::ReadAllText($file))
        if ($doc.DocumentElement.LocalName -eq 'mxfile') {
            foreach ($diagram in @($doc.DocumentElement.SelectNodes('diagram'))) {
                if ($null -ne $diagram.SelectSingleNode('mxGraphModel') -or [string]::IsNullOrWhiteSpace($diagram.InnerText)) { continue }
                $inner = Read-Xml (Expand-Page $diagram.InnerText)
                $diagram.InnerText = ''
                [void]$diagram.AppendChild($doc.ImportNode($inner.DocumentElement, $true))
            }
            $doc.DocumentElement.SetAttribute('compressed', 'false')
        }
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Indent = $true; $settings.OmitXmlDeclaration = $true; $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
        $buffer = New-Object System.IO.StringWriter
        $writer = [System.Xml.XmlWriter]::Create($buffer, $settings)
        $doc.Save($writer); $writer.Dispose()
        if ($Write) {
            [System.IO.File]::WriteAllText($file, $buffer.ToString(), (New-Object System.Text.UTF8Encoding($false)))
            Write-Json ([ordered]@{ file = $file; written = $true })
        }
        else { Write-Output $buffer.ToString() }
    }
}
