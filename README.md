# Agilent-E8403A-VXI-Mainframe-Control
Agilent E8403A VXI mainframe control software. Using a RPI 5 to connect to the GPIB adapter. Host computer can then command the RPI 5 via SSH 

## This is a work in progress! Currently only supports HP (now Keysight) E1368A and E1472A cards. However, I will shortly add the Racal 3271 as I had some old code to get that working already (another repo). It will also support E1473 cards which are just an extension to the E1472A with no address of their own.

## The GUI
<img width="990" height="941" alt="image" src="https://github.com/user-attachments/assets/3e010652-23d3-428d-97ce-2770653839d8" />

## What do you need to run this?
- Raspberry Pi 5 with 64-bit Lite OS, specifically I am using Debian GNU/Linux 13 (trixie) with kernal Linux 6.18.39+rpt-rpi-2712. The build may need adapting for other configs.
- GPIB to USB adapter from NI
- VXI Mainframe E8403A with an Agilent E1406A controller
- E1368A or E1472A cards (any combination should work, just ensure the card address is set to a usable value with the DIP switches - the controller serial needs to be able to see them)
- Host PC, any should do as long as you can SSH to the Pi and have a web browser. It needs to of course be on the same network 

## Basic Commands
I'll add build commands later and the rest of it but this is more for my own quick reference  
Starting the service: sudo systemctl start vxi-broker  
Verify status: vxi status  
Opening web service from host PC: http://<your-pi-ip>:8080   
