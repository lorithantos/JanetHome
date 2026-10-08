// Does not build: the output names an identifier that is spelt differently from the
// parameter it means, which the compiler reports as BCP057 (name does not exist).
param location string = 'westeurope'

output where string = locaton
