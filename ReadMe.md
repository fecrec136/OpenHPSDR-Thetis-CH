# Latest Release v2.8.12 November 04, 2021

# 2.8.12 (2021-11-04)
- Formal release with no timeout

# 2.8.12 Beta 5 (2021-08-05)
- Corrected problems with drive slider bar
- Added timeout for read thread to stop lock-ups when disconnecting for HL2
- Fixed missing tune signal in protocol when tuning
- Updated I2C control to better handle fault conditions
- Added control to allow 10 MHz reference input on CL1
- Added controls to allow CL2 to output a given frequency
- Updated calculations for power, PA current and signal strength to be more believable
 
# 2.8.12 Beta 4 (2021-05-08)
- Added facility to read and write to devices on both I2C buses

# 2.8.12 Beta 3 (2021-05-01)
- Added option to cause HL2 reset on disconnect
- Corrected bug where CW on VFO A blocked Tx on VFO Beta
- Changed Drive control to better reflect use with HL2's 16 stage Tx attenuator 
- Disabled packet reorder as it was causing Thetis to crash if it couldn't connect to HL2

# 2.8.12 Beta 2 (2021-03-21)
- Corrected Tx filter selection when Tx'ing on VFO B
- Added option to allow CAT to control frequency of VFO B
- Corrected bug where 160M Tx filter wasn't ticked

# 2.8.12 Beta 1 (2021-02-25)
- Added 384K sampling
- Corrected power display
- Corrected S meter for correct display of received signal
- Added support for correct display of Gateway version and number of Rx's
- Added support for 61 attenuator steps
- Added support auto attenuator 
- Added support for display temperature and PA current
- Added support for band voltage
- Added support for Tx buffer latency and PTT hang
- Added support for CWX key and PTT via MIDI
- Added different database path for HL2
- Added support for separate Rx antenna
- Added support for N2ADR filter selection 
- Fixed bug where PS settings were not stored correctly 
