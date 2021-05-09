
# Latest Release v2.8.12 Beta 4 May 08, 2021

# 2.8.12 Beta 4 (2021-05-08)

- added facility to read and write to devices on both I2C buses

# 2.8.12 Beta 3 (2021-05-01)

- added option to cause HL2 reset on disconnect
- corrected bug where CW on VFO A blocked Tx on VFO Beta
- changed Drive control to better reflect use with HL2's 16 stage Tx attenuator 
- disabled packet reorder as it was causing Thetis to crash if it couldn't connect to HL2

# 2.8.12 Beta 2 (2021-03-21)

- corrected Tx filter selection when Tx'ing on VFO B
- added option to allow CAT to control frequency of VFO B
- corrected bug where 160M Tx filter wasn't ticked

# 2.8.12 Beta 1 (2021-02-25)
- added 384K sampling
- corrected power display
- corrected S meter for correct display of received signal
- added support for correct display of Gateway version and number of Rx's
- added support for 61 attenuator steps
- added support auto attenuator 
- added support for display temperature and PA current
- added support for band voltage
- added support for Tx buffer latency and PTT hang
- added support for CWX key and PTT via MIDI
- added different database path for HL2
- added support for separate Rx antenna
- added support for N2ADR filter selection 
- fixed bug where PS settings were not stored correctly 
