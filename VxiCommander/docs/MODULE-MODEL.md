# Supported HP switch modules

## HP E1368A

The E1368A is an 18 GHz microwave switch module containing three
independent coaxial SPDT switches. The fitted switches are addressed as
channels `00`, `01`, and `02`.

The software exposes four operations:

- close a switch: `CLOS (@ccnn)`
- open a switch: `OPEN (@ccnn)`
- query close state: `CLOS? (@ccnn)`
- query open state: `OPEN? (@ccnn)`

`cc` is the switchbox card number and `nn` is `00` through `02`.

## HP E1472A

The E1472A is the 50-ohm RF multiplexer and contains six independent 1x4 banks.

Valid local channels are:

- bank 0: `00` to `03`
- bank 1: `10` to `13`
- bank 2: `20` to `23`
- bank 3: `30` to `33`
- bank 4: `40` to `43`
- bank 5: `50` to `53`

Only one channel in each bank can be connected to its common at a time.
At power-on/reset, channel `n0` is connected for each bank.

## HP E1473A expanders

The E1473A is the 50-ohm expander.
They are not independently addressed switchbox instruments.

A base E1472A controls up to two E1473A expanders:

- module `00`: base E1472A
- module `01`: first E1473A expander
- module `02`: second E1473A expander

The multiplexer driver therefore supports module numbers 0 through 2.
Do not assign the E1472A driver directly to a separately discovered
expander logical address unless it represents the base multiplexer
switchbox card.
