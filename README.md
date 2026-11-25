# ScamperLAN

An experimental BepInEx IL2CPP plugin for private Scamper LAN sessions.

## Requirements

- Scamper and a compatible BepInEx 6 IL2CPP installation
- Radmin VPN active on the host and every guest
- The same ScamperLAN build on all players

## Install

Copy `ScamperLAN.dll` to `Scamper/BepInEx/plugins` on each PC, then launch the game.

## Join a game

The host opens **Type Code** on the TV and presses Enter with their prefilled code. Give that code to guests. Guests open **Type Code**, enter the host's code, and press Enter.

The six-character code represents the host's Radmin IPv4 address. It is not a password or authentication. Use only on a trusted private network; the mod uses UDP port 7777.