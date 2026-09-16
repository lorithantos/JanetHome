# JANET-SHIM
<#
.SYNOPSIS
    Records an investigation topic on the thread item list.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Adding does NOT take focus unless -Active is passed. That separation is the whole reason
    this is a list rather than the push/pop stack it replaced: noting work you are not doing
    now has to cost nothing, or you stop noting it.

    Refuses a topic that already exists rather than creating a second one the selectors cannot
    tell apart.

.PARAMETER Topic
    What the investigation is about. One line, distinctive enough to select on later.

.PARAMETER Notes
    Detail too small or too fresh to be worth a research.json node. CAPPED at 8,000 characters
    (JANET_NOTES_BUDGET overrides), and an add over the ceiling is refused whole: there is no
    older half to archive, so the automatic archiving that rescues an over-ceiling append to an
    existing item has nothing to work with here. Long-form belongs in a catalogued note -- write
    it to notes\<slug>.md, `janet research add` it as note.<slug>, and put the id in -Refs.

.PARAMETER Next
    The resume cursor: the one thing to do first on return. Capped at 1,000 characters, with no
    override: a cursor needing a thousand characters is notes wearing the wrong label.

.PARAMETER Refs
    Catalog node ids carrying the context for this topic.

.PARAMETER Active
    Also take focus, parking whatever held it.

.PARAMETER Area
    Which project or area this belongs to. Free text, and a STORED label rather than one
    derived from the topic: splitting topics on their first colon was measured to produce 12
    groups for 16 topics and to split single projects across several of them. Omit it and the
    item is '(unfiled)', which is one real group and not a guess at which project it resembles.

    Worth setting. The list is shared by every repo on this machine, so an unfiled item can
    only be found by reading all of them.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Topic,
    [string]$Notes = '',
    [string]$Next = '',
    [string[]]$Refs = @(),
    [string]$Area = '',
    [switch]$Active,
    [string]$Path = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'add', '--topic', $Topic)

# Repeated flags rather than a delimited string: the CLI accumulates them, so no caller has to
# know a separator. Getting that wrong is how a multi-value argument silently becomes one.
foreach ($value in @($Refs)) { if ($value) { $arguments += @('--ref', $value) } }

if ($Notes) { $arguments += @('--notes', $Notes) }
if ($Next) { $arguments += @('--next', $Next) }
if ($Area) { $arguments += @('--area', $Area) }
if ($Path) { $arguments += @('--path', $Path) }
if ($Active) { $arguments += '--active' }

& $janet @arguments
exit $LASTEXITCODE
