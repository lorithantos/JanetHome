#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\lib\SchemaBody.ps1: what Test-OutputContracts.ps1's change check
    treats as a format change.

.DESCRIPTION
    Decided 2026-10-21: schema descriptions are prose, not contract, so a description-only
    edit must compare equal and pass without a contract bump. Everything structural must
    still compare different, and so must a PROPERTY named description, which is data the
    stripping has to walk past. Each case compares two schema texts the way the gate does:
    Get-SchemaBody on both sides, then -cne.
#>

BeforeAll {
    . (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\lib\SchemaBody.ps1')

    $script:base = @'
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$janet": { "contract": 3, "script": "scripts\\X.ps1" },
  "description": "Top-level prose.",
  "type": "object",
  "additionalProperties": false,
  "required": ["ok", "description"],
  "properties": {
    "ok": { "type": "boolean", "description": "Whether it worked." },
    "description": { "type": "string", "description": "A property that happens to be called description." },
    "mode": { "enum": ["a", "b"], "description": "Which mode." },
    "items": {
      "type": "array",
      "items": { "type": "object", "properties": { "n": { "type": "integer", "description": "Count." } }, "description": "One row." }
    },
    "either": { "anyOf": [ { "type": "null", "description": "Absent." }, { "const": { "description": "an instance, not a keyword" } } ] }
  },
  "$defs": { "row": { "type": "string", "description": "A def." } }
}
'@

    function Test-FormatChanged {
        param([string]$Before, [string]$After)
        return ((Get-SchemaBody $Before) -cne (Get-SchemaBody $After))
    }
}

Describe 'Get-SchemaBody: descriptions are prose' {
    It 'treats a description-only edit, at every level, as no format change' {
        $after = $script:base.
            Replace('Top-level prose.', 'Reworded top level.').
            Replace('Whether it worked.', 'True when it worked.').
            Replace('A property that happens to be called description.', 'Free text.').
            Replace('"description": "One row."', '"description": "A row, reworded."').
            Replace('"description": "Absent."', '"description": "Missing."').
            Replace('"description": "A def."', '"description": "A definition."')

        Test-FormatChanged $script:base $after | Should -BeFalse
    }

    It 'treats a description added or removed as no format change' {
        $after = $script:base.Replace(', "description": "Count."', '').Replace('"type": "boolean"', '"type": "boolean", "description": "new"')

        Test-FormatChanged $script:base $after | Should -BeFalse
    }

    It 'still counts a structural edit: <label>' -ForEach @(
        @{ label = 'a type';                 from = '"ok": { "type": "boolean"';  to = '"ok": { "type": "string"' }
        @{ label = 'required';               from = '"required": ["ok", "description"]'; to = '"required": ["ok"]' }
        @{ label = 'an enum value';          from = '"enum": ["a", "b"]';          to = '"enum": ["a", "b", "c"]' }
        @{ label = 'additionalProperties';   from = '"additionalProperties": false'; to = '"additionalProperties": true' }
        @{ label = 'a nested items type';    from = '"n": { "type": "integer"';    to = '"n": { "type": "number"' }
        @{ label = 'a const instance';       from = '"an instance, not a keyword"'; to = '"another instance"' }
        @{ label = 'a $defs type';           from = '"row": { "type": "string"';   to = '"row": { "type": "integer"' }
    ) {
        $after = $script:base.Replace($from, $to)
        $after | Should -Not -Be $script:base

        Test-FormatChanged $script:base $after | Should -BeTrue
    }

    It 'still counts a property named description: removing it is a format change' {
        $after = $script:base.Replace('"description": { "type": "string", "description": "A property that happens to be called description." },', '')
        $after | Should -Not -Be $script:base

        Test-FormatChanged $script:base $after | Should -BeTrue
    }

    It 'still counts the type of a property named description' {
        $after = $script:base.Replace('"description": { "type": "string"', '"description": { "type": "integer"')

        Test-FormatChanged $script:base $after | Should -BeTrue
    }

    It 'ignores the $janet block, as before' {
        $after = $script:base.Replace('"contract": 3', '"contract": 4')

        Test-FormatChanged $script:base $after | Should -BeFalse
    }
}
