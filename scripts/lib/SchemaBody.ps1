# Get-SchemaBody: what Test-OutputContracts.ps1's change check compares, so a schema edit
# counts as a FORMAT change only when it changes the format. Split out of that script on
# 2026-10-22 so tests can call it without running the gate. Dot-source it; it defines
# functions and changes nothing else.

# Keywords whose value is a map from a NAME to a schema. The names are data -- a property
# can be called "description" -- so they are walked into, never stripped.
$script:SchemaNameMaps = @('properties', 'patternProperties', '$defs', 'definitions', 'dependentSchemas')

# Keywords whose value is one schema, or (items, in older drafts) an array of them.
$script:SchemaValued = @('items', 'additionalProperties', 'additionalItems', 'not', 'if', 'then', 'else',
                         'contains', 'propertyNames', 'unevaluatedItems', 'unevaluatedProperties')

# Keywords whose value is an array of schemas.
$script:SchemaArrays = @('allOf', 'anyOf', 'oneOf', 'prefixItems')

function Remove-SchemaDescription {
    # Strips the 'description' KEYWORD from a parsed schema (ConvertFrom-Json -AsHashtable),
    # in place, at every level that is a schema. Decided by Lori 2026-10-21: descriptions are
    # prose about the format, not the format, and a gate that demands a contract bump to fix
    # a sentence fires on correct work.
    #
    # Keyword-aware rather than "every key named description, anywhere", because two places
    # hold that name as DATA: a property literally named description (under 'properties' and
    # the other name maps), and the values of enum, const, default and examples, which are
    # instances rather than schemas. Neither is walked as a schema, so neither loses anything.
    # Only a string value is stripped: the keyword is always a string, and anything else in
    # that slot is malformed enough that it should still count as a change.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Edits an in-memory parsed schema; changes no system state.')]
    param($Schema)
    if ($Schema -isnot [System.Collections.IDictionary]) { return }

    if ($Schema.Contains('description') -and $Schema['description'] -is [string]) {
        $Schema.Remove('description')
    }

    foreach ($key in @($Schema.Keys)) {
        $value = $Schema[$key]
        if ($script:SchemaNameMaps -contains $key -and $value -is [System.Collections.IDictionary]) {
            foreach ($name in @($value.Keys)) { Remove-SchemaDescription $value[$name] }
        }
        elseif ($script:SchemaValued -contains $key) {
            if ($value -is [System.Collections.IDictionary]) { Remove-SchemaDescription $value }
            elseif ($value -is [System.Collections.IList]) { foreach ($s in $value) { Remove-SchemaDescription $s } }
        }
        elseif ($script:SchemaArrays -contains $key -and $value -is [System.Collections.IList]) {
            foreach ($s in $value) { Remove-SchemaDescription $s }
        }
    }
}

function Get-SchemaBody {
    <#
    .SYNOPSIS
        The schema with its $janet metadata block and every 'description' keyword removed,
        canonicalised so that reformatting or rewording is not mistaken for a format change.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    $parsed = $Text | ConvertFrom-Json -AsHashtable
    $parsed.Remove('$janet')
    Remove-SchemaDescription $parsed

    return ($parsed | ConvertTo-Json -Depth 30 -Compress)
}
