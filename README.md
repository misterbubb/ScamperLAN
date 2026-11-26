# ScamperLAN

An experimental BepInEx IL2CPP plugin for private Scamper LAN sessions.

## Requirements

- Scamper and a compatible BepInEx 6 IL2CPP installation
- A shared IPv4 network, such as the same local Wi-Fi or Ethernet network, or the same Radmin VPN
- The same ScamperLAN build on all players

## Install

Copy `ScamperLAN.dll` to `Scamper/BepInEx/plugins` on each PC, then launch the game.

## Join a game

Select **LAN** on the TV before hosting or joining. LAN stays selected after you leave a room. Each fresh launch starts in **Online**, and you can switch modes between sessions.

1. **Host a room.** Open **Type Code**, enter an unused six-character code made of letters and numbers, and press **Join**. The game creates a LAN room with that code.
2. **Join a room.** Open **Find Code**, select the host's room, and press **Join**. The browser lists joinable rooms from your local network and Radmin together. You can also use **Type Code** to enter the host's exact code. Submitting an unused code creates a room.

LAN rooms appear in **Find Code** automatically. If a firewall blocks LAN traffic, allow incoming UDP on the host for ports **7777** (game) and **7778** (discovery).

## Credits

- **Contributors** — misterbubb and J_axon
- **J_axon** - Major help with testing this with me, love you bro bro.
