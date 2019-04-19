const net = require('net');

const { SecurityType, ServerMessage, ClientMessage, MessageType } = require('./vncconst');

class VncInterceptorClient {
    constructor() {
        this.client = null;

        // PacketStatus
        this.PacketStatus = {
            ProtocolVersionHandshake: false,
            SecurityHandshake: false,
            Authentication: false,
            SecurityResult: false,
            ServerInit: false,
        };

        this.desktopInfo = {};
    }

    Connect(host, port, security) {
        this.security = security;
        try {
            this.client = net.connect({ host, port }, () => this.callback({ status: true, error: null }));
        } catch (e) {
            this.callback({ status: false, error: e });
            return;
        }
        this.client.on('data', (data) => this._onRecieve(this, data));
        this.client.on('end', () => this.client = null);
    }

    Send(data) {
        if (!this.client) {
            this.callback({ status: false, error: `Undefined Data Type: ${data.type}` });
            return;
        }

        switch (data.type) {
            case MessageType.ProtocolVersionHandshake: {
                const { version } = data;

                this.protocolVersion = version;
                this.PacketStatus.ProtocolVersionHandshake = true;

                const RFB = `RFB 00${version.major}.00${version.minor}\n`;
                this.client.write(RFB);
                break;
            }
            case MessageType.SecurityHandshake: {
                this.client.write(data.security);
                this.PacketStatus.Security = true;
                break;
            }
            case MessageType.Authentication: {
                const challenge = Buffer.from(data.challenge);
                this.client.write(challenge);
                this.PacketStatus.Authentication = true;
                break;
            }
            case MessageType.ClientInit: {
                this.client.write(data.shareDesktop ? 1 : 0);
                this.PacketStatus.SecurityResult = true;
                break;
            }
            case MessageType.SetPixelFormat: {
                const buf = Buffer.alloc(20, 0);
                buf[0] = ClientMessage.SetPixelFormat;
                this._makePixelFormat(data.info).copy(buf, 4);

                this.client.write(buf);
                break;
            }
            case MessageType.SetEncodings: {
                const buf = Buffer.alloc(4 * (data.encoding.length + 1), '\0');
                buf[0] = ClientMessage.SetEncodings;
                buf.writeUIntBE(data.encoding.length, 2, 2);
                for (let i = 0; i < data.encoding.length; ++i) {
                    buf.writeIntBE(data.encoding[i], 4 + 4 * i, 4);
                }

                this.client.write(buf);
                break;
            }
            case MessageType.FramebufferUpdateRequest: {
                const buf = Buffer.alloc(10, 0);
                buf[0] = ClientMessage.FramebufferUpdateRequest;
                buf[1] = data.incremental ? 1 : 0;
                buf.writeUIntBE(data.pos.x, 2, 2);
                buf.writeUIntBE(data.pos.y, 2, 2);
                buf.writeUIntBE(data.width, 2, 2);
                buf.writeUIntBE(data.height, 2, 2);

                this.client.write(buf);
                break;
            }
            case MessageType.KeyEvent: {
                const buf = Buffer.alloc(8, 0);
                buf[0] = ClientMessage.KeyEvent;
                buf[1] = data.isDown ? 1 : 0;
                Buffer.from(data.key).copy(buf, 4);

                this.client.write(buf);
                break;
            }
            case MessageType.PointerEvent: {
                const buf = Buffer.alloc(6, 0);
                buf[0] = ClientMessage.PointerEvent;
                buf[1] = data.pointerInfo;
                buf.writeUIntBE(data.pos.x, 2, 2);
                buf.writeUIntBE(data.pos.y, 2, 2);

                this.this.client.write(buf);
                break;
            }
            case MessageType.ClientCutText: {
                const buf = Buffer.alloc(data.text.length + 8);
                buf[0] = ClientMessage.ClientCutText;
                buf.writeUIntBE(data.text.length, 4, 4);
                buf.write(data.text, 8);

                this.client.write(buf);
                break;
            }
            default: {
                this.callback({ status: false, error: `'Undefined Data Type: ${data.type}` });
            }
        }
    }

    Close() {
        if (this.client) {
            this.client.end();
        }
    }

    setCallback(callback) {
        this.callback = callback;
    }

    _onRecieve(context, data) {
        if (!context.PacketStatus.ProtocolVersionHandshake) {
			/*
			 * If we didn't get Protocol Handshaked, AND the packet isn't
			 * Protocol Handshake packet, throw error.
			 */
            context.callback(context._handleProtocol(data));
        }
        else if (!context.PacketStatus.SecurityHandshake) {
			/*
			 * If we didn't get Security Handshaked, AND the packet isn't
			 * Security Handshake packet, throw error.
			 */
            context.callback(context._handleSecurity(data));
        }
        else if (!context.PacketStatus.Authentication) {
			/*
			 * If we didn't get authenticated, AND the packet isn't
			 * Authentication Challenge packet, throw error.
			 */
            context.callback(context._handleAuthentication(data));
        }
        else if (!context.PacketStatus.SecurityResult) {
			/*
			 * If we didn't get Security Result, AND the packet isn't
			 * Security Result packet, throw error.
			 */
            context.callback(context._handleSecurityResult(data));
        }
        else if (!context.PacketStatus.ServerInit) {
            context.callback(context._handleServerInit(data));
        }
        else {
            const messageType = data[0];
            switch (messageType) {
                case ServerMessage.FramebufferUpdate: {
                    context.callback(context._handleFrameUpdate(data));
                    break;
                }
                case ServerMessage.SetColorMapEntries: {
                    context.callback(context._handleSetColorMap(data));
                    break;
                }
                case ServerMessage.Bell: {
                    context.callback(context._handleBell());
                    break;
                }
                case ServerMessage.ServerCutText: {
                    context.callback(context._handleCutText(data));
                    break;
                }
                default: {
                    const msg = `Undefined Message Type: ${messageType}`;
                    context.callback({ status: false, error: msg });
                }
            }
        }
    }

    _handleProtocol(data) {
        // Check Protocol Handshake Packet
        // RFC6143 7.1.1. ProtocolVersion Handshake
        const PATTERN_HANDSHAKE = /RFB 00(\d)\.00(\d)\n/;
        const versionInfo = PATTERN_HANDSHAKE.exec(data);

        // If it isn't a Protocol Handshake Packet
        if (!versionInfo) {
            const msg = 'Hadn\'t recieved Protocol Handshake!';
            return { status: false, error: msg };
        }

        const isBrokenData = data.length !== 12;
        if (isBrokenData) {
            const msg = data.slice(data.indexOf(0x1a, 13) + 1, data.length);
            return { status: false, error: msg };
        }

        const VNCVersion = {
            version: parseFloat(VNCVersion.major + '.' + VNCVersion.minor),
            major: parseInt(versionInfo[1]),
            minor: parseInt(versionInfo[2])
        };

        return {
            status: true,
            type: MessageType.ProtocolVersionHandshake,
            version: VNCVersion
        };
    }

    _handleSecurity(data) {
        // Check Security Handshake Packet
        // RFC6143 7.1.2. Security Handshake

        // !!IMPORTANT!!
        // The protocol is diffrent here by the version
        // RFC6143 Appendix A. Differnces in Earlier Protocol Versions
        if (this.protocolVersion.version === 3.3) {
            // In Version 3.3, Server decides the security type
            const security = data.readUIntBE(0, 3);

            const isNoneSecurity = this.security === SecurityType.None;
            if (isNoneSecurity) {
                this.PacketStatus.Authentication = true;
                this.PacketStatus.SecurityResult = true;
            }

            //No responce in version 3.3
            this.PacketStatus.SecurityHandshake = true;

            return { status: true, type: MessageType.SecurityHandshake33, security };
        }

        const list = this._getSupportedSecurity(data);
        return (list.status)
            ? { status: true, type: MessageType.SecurityHandshake, security: list.type }
            : { status: false, error: list.error };
    }

    _handleAuthentication(data) {
        // Challenge VNCAuth
        // RFC6143 7.2.2. VNC Authentication

        // VNC Authentication Challenge message size is fixed to 16 bytes.
        const isBrokenData = data.length !== 16;
        return (isBrokenData)
            ? { status: false, error: `Challenge Message corrupted. Expected 16 bytes, Got ${data.length}bytes.` }
            : { status: true, type: MessageType.VNCAuthentication, challenge: data.toString() };
    }

    _handleSecurityResult(data) {
        // Check SecurityResult Packet
        // RFC6143 7.1.3. SecurityResult Handshake
        const securityResult = data.readUIntBE(0, 4);
        switch (securityResult) {
            case 0: {
                return { status: true, type: MessageType.SecurityResultHandshake };
            }
            case 1: {
                // !!IMPORTANT!!
                // The protocol is diffrent here by the version
                // RFC6143 Appendix A. Differnces in Earlier Protocol Versions
                let msg;
                switch (this.VNCVersion.version) {
                    case 3.3:
                    case 3.7: {
                        msg = 'No reason Provided because of the Protocol Version.';
                        return { status: false, type: MessageType.SecurityResultHandshake, error: msg };
                    }
                    case 3.8: {
                        const reasonLength = data.readUIntBE(4, 4);
                        msg = data.slice(8, 8 + reasonLength);
                        return { status: false, type: MessageType.SecurityResultHandshake, error: msg };
                    }
                }
                break;
            }
            default: {
                const msg = `Undefined SecurityResult:${result}`;
                return { status: false, type: MessageType.SecurityResultHandshake, error: msg };
            }
        }
    }

    _handleServerInit(data) {
        // Read ServerInit Packet
        // RFC6143 7.3.2 ServerInit
        // RFC6143 7.4 Pixel Format Data Structure
        this.desktopInfo.width = data.readUIntBE(0, 2);
        this.desktopInfo.height = data.readUIntBE(2, 2);

        // ServerPixelFormat
        this.desktopInfo.bitsPerPixel = data[4];
        this.desktopInfo.depth = data[5];
        this.desktopInfo.isBigEndian = data[6] !== 0;
        this.desktopInfo.isTrueColor = data[7] !== 0;
        this.desktopInfo.maxRed = data.readUIntBE(8, 2);
        this.desktopInfo.maxGreen = data.readUIntBE(10, 2);
        this.desktopInfo.maxBlue = data.readUIntBE(12, 2);
        this.desktopInfo.shiftRed = data[14];
        this.desktopInfo.shiftGreen = data[15];
        this.desktopInfo.shiftBlue = data[16];

        const namelen = data.readUIntBE(20, 4);
        this.desktopInfo.name = data.slice(24, 24 + namelen).toString();

        this.PacketStatus.ServerInit = true;

        return { status: true, type: MessageType.ServerInit, info: this.desktopInfo };
    }

    _handleFrameUpdate(data) {
        const rectArr = [];

        const numRect = data.readUIntBE(2, 2);
        for (let i = 0; i < numRect; ++i) {
            rectArr.push({
                pos: {
                    x: data.readUIntBE(4 + 12 * i, 2),
                    y: data.readUIntBE(6 + 12 * i, 2)
                },
                width: data.readUIntBE(8 + 12 * i, 2),
                height: data.readUIntBE(10 + 12 * i, 2),
                encoding: data.readIntBE(12 + 12 * i, 4)
            });
        }

        return { status: true, type: MessageType.FramebufferUpdate, rect: rectArr };
    }

    _handleSetColorMap(data) {
        const colorArr = [];

        //I don't know where it is used
        const firstColor = data.readUIntBE(2, 2);
        const numColor = data.readUIntBE(4, 2);
        for (let i = 0; i < numColor; ++i) {
            colorArr.push({
                Red: data.readUIntBE(6 + 6 * i, 2),
                Green: data.readUIntBE(8 + 6 * i, 2),
                Blue: data.readUIntBE(10 + 6 * i, 2)
            });
        }

        return {
            status: true,
            type: MessageType.SetColorMapEntries,
            firstColor,
            color: colorArr
        };
    }

    _handleBell() {
        return { status: true, type: MessageType.Bell };
    }

    _handleCutText(data) {
        const length = data.readUIntBE(4, 4);
        const text = data.slice(8, 8 + length).toString();

        return { status: true, type: MessageType.ServerCutText, text };
    }

    _getSupportedSecurity(data) {
        // First Byte of Packet Should be U8
        const nType = data[0];

        if (nType === 0) {
            const reasonLength = data.readUIntBE(1, 4);
            const reason = data.slice(5, 5 + reasonLength);
            return { status: false, error: reason };
        }

        const type = [];
        for (let i = 0; i < nType; i++) {
            type[i] = data[i + 1];
        }

        return { status: true, type: type };
    }

    _makePixelFormat(data) {
        //RFC6143 7.4. Pixel Format Data Structure
        const buf = Buffer.alloc(16, 0);

        buf[0] = data.bitsPerPixel;
        buf[1] = data.depth;
        buf[2] = data.isBigEndian ? 1 : 0;
        buf[3] = data.isTrueColor ? 1 : 0;
        buf.writeUIntBE(data.maxRed, 4, 2);
        buf.writeUIntBE(data.maxGreen, 6, 2);
        buf.writeUIntBE(data.maxBlue, 8, 2);
        buf[10] = data.shiftRed;
        buf[11] = data.shiftGreen;
        buf[12] = data.shiftBlue;

        return buf;
    }
}

module.exports = VncInterceptorClient;