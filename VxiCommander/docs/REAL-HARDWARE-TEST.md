# Real hardware validation

Verified reference topology:

- Linux-GPIB board: `gpib0`
- E1406A SYSTEM: PAD 10, SAD 0
- SWITCHBOX: PAD 10, SAD 15
- E1406A identification: `HEWLETT-PACKARD,E1406A,0,A.09.00`
- SWITCHBOX identification: `HEWLETT-PACKARD,SWITCHBOX,0,A.07.00`

Linux-GPIB encodes a real secondary address `N` as `0x60 + N`. A value of `0` passed to `ibdev` means secondary addressing is disabled; it does not mean SAD 0.

## Discovery

```bash
vxi discover
```

The broker probes PAD 0 through 30 at SAD 0, identifies E1406A controllers with `*IDN?`, queries `VXI:CONF:DLIS?`, parses the returned logical-address records, extracts the SWITCHBOX secondary address, and verifies that endpoint with `*IDN?` and `SYST:ERR?`.

## Inventory confirmation

The E1406A A.09.00 firmware may report logical addresses without reliable physical slots or module model names. Use the localhost web UI to confirm:

- module model/driver;
- physical slot;
- logical address;
- switchbox card number.

## E1472A guarded relay test

Remove RF power and disconnect any sensitive equipment first.

The E1472A channel format is `ccmmnn`:

- `cc`: switchbox card number (01-99)
- `mm`: module 00, or expander 01/02
- `nn`: bank/channel (`00-03`, `10-13`, ... `50-53`)

Use the web UI in this order:

1. Dry-run query, close, and open.
2. Confirm the generated channel address.
3. Type `SWITCH RELAY` exactly.
4. LIVE close.
5. LIVE verify (`CLOS?`).
6. LIVE restore/open.

The query is software readback; it does not prove the relay contacts physically changed. Confirm with a continuity meter or RF measurement where appropriate.
