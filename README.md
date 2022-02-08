# HLL Vip Tool
HLL VIP management tool to import/export VIPs on multiple servers

# Features 

- Manage a list of server for future use
  - Add
  - Delete
- Export VIPs from Multiple servers
  - The tool will download all the VIPs and merge-distinct them. The disctinct is Case insensitive
- Import a list of VIPs to multiple servers

# Server mode

 Start the tool with `-m Server -d 15` to enable the server mode. This mode requires pre-filled `vips.csv` and `server.json` files
 
 You can change the desired delay (in minutes) with -d