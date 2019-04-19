/*
 * noVNC: HTML5 VNC client
 * Copyright (C) 2012 Joel Martin
 * Copyright (C) 2018 Samuel Mannehed for Cendio AB
 * Copyright (C) 2018 Pierre Ossman for Cendio AB
 * Licensed under MPL 2.0 (see LICENSE.txt)
 *
 * See README.md for usage and integration instructions.
 *
 */

import Inflator from "../inflator.js";

export default class ZlibDecoder {
    constructor() {
        this._ctl = null;
        this._zlibs = new Inflator();
        this._len = 0;
    }

    decodeRect(x, y, width, height, sock, display, depth) {

        const uncompressedSize = width * height * 4;

        let data = this._readData(sock);
        if (data === null) {
            return false;
        }

        this._zlibs.reset();
        data = this._zlibs.inflate(data, true, uncompressedSize);
        if (data.length != uncompressedSize) {
            throw new Error("Incomplete zlib block");
        }

        display.blitImage(x, y, width, height, data, 0);
        //display.imageRect(x, y, "image/jpeg", data);

        return true;
    }

    _readData(sock) {

        if (this._len <= 0) {

            while (sock.rQwait("ZLIB", 4)) {
                return null;
            }

            let byte1 = sock.rQshift8();
            this._len = byte1 << 24;
            let byte2 = sock.rQshift8();
            this._len |= byte2 << 16;
            let byte3 = sock.rQshift8();
            this._len |= byte3 << 8;
            let byte4 = sock.rQshift8();
            this._len |= byte4;
        }

        if (sock.rQwait("ZLIB", this._len)) {
            return null;
        }

        let data = sock.rQshiftBytes(this._len);

        this._len = 0;

        return data;
    }
}
