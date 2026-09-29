# WiFi Share Testing

A browser-based, free-to-host experiment for sending a small file directly between two browsers over the internet. The file travels over a WebRTC data channel, not through this website.

## Try it

1. Open the hosted page on both devices. Both people need to keep the page open and stay online.
2. On the first device, accept the direct-connection warning and choose **Create a connection**. Copy the offer code and send it to the other person using a messaging app.
3. On the second device, accept the warning, choose **Join a connection**, paste the offer, and create an answer code. Send that code back.
4. Paste the answer on the first device and connect. Either person can then choose a file of up to 50 MB. The receiving person must approve it.

Offer and answer codes contain WebRTC connection information, including network addresses. Share them only with the intended recipient. Direct mode can expose your public IP address to the peer and the STUN service. WebRTC encrypts data-channel traffic, but direct connections may fail on networks that require a relay. This prototype has no TURN or cloud-storage fallback and is not integrated with the PocketBridge Windows app.

## Hosting

The page is static and can be hosted with GitHub Pages. Only the offer/answer codes are exchanged through another messaging app; GitHub Pages does not carry the files. A public STUN server helps the browsers find a direct route, but does not relay the file.

## Prototype limits

- One file at a time, up to 50 MB.
- Both pages must remain open until the transfer finishes.
- No relay fallback, offline delivery, folder transfer, QR pairing, or resume after a disconnect.
- This proof of concept prioritizes zero server bandwidth cost over IP-address privacy and universal network compatibility.

This repository does not assign an open-source license.
