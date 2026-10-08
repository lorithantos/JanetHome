// Builds, with exactly one linter finding: the parameter below is never used, so
// no-unused-params reports it at its default level (warning).
param unused string = 'nothing reads this'

output answer int = 42
