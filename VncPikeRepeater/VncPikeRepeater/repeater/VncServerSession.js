var net = require('net');
var BufferQueue = require('./BufferQueue.js');
var EventEmitter = require('events').EventEmitter;
var DES = require('./des.js');
var zlib = require('zlib');
var crypto = require('crypto');

function VncServerSession(_socket, _repeaterSession) {

    this.events = new EventEmitter();
    this.socket = _socket;
    this.repeaterSession = _repeaterSession;
    this.mode = 0;
    this.work_stage = -1;
    this.buffer = new BufferQueue(10 * 1024 * 1024);
    this.authChallenge = crypto.randomBytes(16);
    this.password = this.repeaterSession.getLookerPassword();
    this.encodings_count = undefined;
    this.encodings = [];
    this.command = undefined;
    this.clipboard_length = undefined;
    this.frameBuffer = undefined;
    this.rectsForTransfer = [];

    //Server data
    this.info = this.repeaterSession.getServerInfo();

    /*this.info =
    {
        name: this.repeaterSession.getServerName(),
        width: 1920,
        height: 1080,
        ver: '3.8',
        pixel_format:
        {
            bits_per_pixel: 32,
            depth: 8,
            big_endian_flag: 0,
            true_colour_flag: 1,
            red_max: 255,
            green_max: 255,
            blue_max: 255,
            red_shift: 16,
            green_shift: 8,
            blue_shift: 0,
        }
    };*/

    //Начинаем обмен с looker
    this.socket.write("RFB 003.008\n", "binary");
    var self = this;
    
    this.socket.on('connect', function () {
        console.log('[VNC Looker] -> Connected');
        self.socket.setTimeout(0);
        self.socket.setNoDelay(true);
    });

    this.socket.on('end', () => {
        this.repeaterSession.removeLooker(this);
    });

    this.writeUInt16BE = function (value_short) {
        var chunk = new Buffer(2);
        chunk.writeUInt16BE(value_short, 0);
        this.socket.write(chunk);
    }

    this.writeUInt32BE = function (value_int) {
        var chunk = new Buffer(4);
        chunk.writeUInt32BE(value_int, 0);
        this.socket.write(chunk);
    }

    this.writeRectangle = function (rect) {
        var chunk = new Buffer(8);
        chunk.writeUInt16BE(rect.x, 0)
        chunk.writeUInt16BE(rect.y, 2)
        chunk.writeUInt16BE(rect.width, 4)
        chunk.writeUInt16BE(rect.height, 6)
        this.socket.write(chunk);
    }

    this.respondToUpdateRequest = function (incremental, region) {

        this.rectsForTransfer = [];
        let buffer_actual = this.repeaterSession.getFrameBuffer();
        let buffer_cached = this.frameBuffer;

        // incremental = 0;

        if (incremental) {

            let bpp = this.info.pixel_format.bits_per_pixel / 8;
            let XDiv = 64 * 2;
            let YDiv = 64 * 2;
            let Stride = bpp * this.info.width;

            for (var xi = region.x; xi < region.x + region.width; xi += XDiv) {
                for (var yi = region.y; yi < region.y + region.height; yi += YDiv) {

                    let subregion = {};
                    subregion.x = xi;
                    subregion.y = yi;
                    subregion.width = XDiv;
                    subregion.height = YDiv;

                    if (subregion.x + subregion.width > this.info.width) {
                        subregion.width = this.info.width - subregion.x;
                    }

                    if (subregion.y + subregion.height > this.info.height) {
                        subregion.height = this.info.height - subregion.y;
                    }

                    let bytes_width = subregion.width * bpp;
                    let need_update = false;

                    for (var iy = subregion.y; iy < subregion.y + subregion.height; iy++) {

                        let srcOffset = (iy * Stride) + (bpp * xi);

                        for (var pos = srcOffset; pos < srcOffset + bytes_width; pos++) {
                            if (buffer_actual[pos] != buffer_cached[pos]) {
                                need_update = true;
                                break;
                            }
                        }

                        if (need_update) {
                            break;
                        }
                    }

                    // Таки надо обновлять
                    if (need_update) {

                        // Буфер для передачи
                        subregion.transfer_buffer = Buffer.alloc(subregion.height * bytes_width);
                        let targetStart = 0;

                        for (var iy = subregion.y; iy < subregion.y + subregion.height; iy++) {
                            let srcOffset = (iy * Stride) + (bpp * subregion.x);

                            //buffer.copy(target, targetStart, sourceStart, sourceEnd);
                            buffer_actual.copy(buffer_cached, srcOffset, srcOffset, srcOffset + bytes_width);
                            buffer_actual.copy(subregion.transfer_buffer, targetStart, srcOffset, srcOffset + bytes_width);

                            targetStart += bytes_width;
                        }

                        this.rectsForTransfer.push(subregion);
                    }
                }
            }

            if (this.rectsForTransfer.length == 0) {

                let subregion = {};

                subregion.x = 0;
                subregion.y = 0;
                subregion.width = this.info.width;
                subregion.height = 1;

                subregion.transfer_buffer = new Buffer(this.info.width * 4);
                buffer_actual.copy(subregion.transfer_buffer, 0, 0, this.info.width * 4);

                this.rectsForTransfer.push(subregion);
            }

        } else {

            // Все полностью отправляем

            let subregion = {};
            subregion.x = region.x;
            subregion.y = region.y;
            subregion.width = region.width;
            subregion.height = region.height;

            subregion.transfer_buffer = Buffer.alloc(buffer_actual.length);

            //buffer.copy(target, targetStart, sourceStart, sourceEnd);
            buffer_actual.copy(buffer_cached, 0, 0, buffer_actual.length);
            buffer_actual.copy(subregion.transfer_buffer, 0, 0, buffer_actual.length);

            this.rectsForTransfer.push(subregion);
        }

        if (this.rectsForTransfer.length > 0) {
            this.sendRectangles();
        }
    }

    this.sendRectangles = function() {

        // Количество передаваемых прямоугольников
        this.writeUInt32BE(this.rectsForTransfer.length);

        for (var i = 0, len = this.rectsForTransfer.length; i < len; i++) {

            let rect = this.rectsForTransfer[i];

            // Параметры прямоугольника
            this.writeRectangle(rect);

            // Енкодер zlib
            this.writeUInt32BE(6);

            // Жмем (gripping your pillow tight)
            rect.transfer_buffer = zlib.deflateSync(rect.transfer_buffer);

            // Длина сжатых данных
            this.writeUInt32BE(rect.transfer_buffer.length);

            // Сами данные
            this.socket.write(rect.transfer_buffer);
        }

        // Чистим все нафиг
        this.rectsForTransfer = [];
    }

    this.socket.on('data', function (data) {

        // Укладывем данные в буфер
        self.buffer.append(data, 0, data.length); 

        while (self.buffer.isHas(1)) {

            switch (self.mode) {

                //Version
                case 0:
                    // Обмениваемся версиями
                    if (!self.buffer.isHas(12)) {
                        return;
                    }

                    let version = self.buffer.get(12).toString();
                    console.log(version);

                    if (version == 'RFB 003.008\n') {
                        self.mode = 1;

                        // Говорим клиенту, что мы понимаем только авторизацию по паролю
                        self.socket.write('\u0001');
                        self.socket.write('\u0002');
                    } else {
                        console.log('Error:unavailable protocol vertion');
                        self.socket.end();
                    }
                    break;
                    break;

                case 1:

                    // Принял ли клиент вариант обмена паролями
                    if (!self.buffer.get(1).readUInt8(0) == 2) {
                        console.log('Error:client do not want password challenge');
                        self.socket.end();
                    }

                    // Отправили челендж
                    self.socket.write(self.authChallenge);
                    self.mode = 2;

                    break;

                case 2:

                    // Принимаем пароль
                    if (!self.buffer.isHas(16)) {
                        return;
                    }

                    var pass_c = self.buffer.get(16);
                                       
                    const passwordChars = self.password.split('').map(c => c.charCodeAt(0));

                    // Совпадают и пароли ?
                    const is_auth = (new DES(passwordChars)).compare(self.authChallenge, pass_c);;

                    if (!is_auth) {
                        console.log('Error:access denied!');

                        // Говорим клиенту что он кругом не прав и отключаемся
                        self.writeUInt32BE(1);
                        self.socket.end();
                        return;
                    }

                    // Мы довольны, велкам, йдемо далi
                    self.writeUInt32BE(0);
                    self.mode = 3;

                    break;

                case 3:

                    // Desktop data
                    let shareDesktopSetting = self.buffer.get(1).readUInt8(0);

                    var chunk = new Buffer(2 + 2 + 16 + 4 + self.info.name.length)
                    chunk.writeUInt16BE(self.info.width, 0)
                    chunk.writeUInt16BE(self.info.height, 2)
                    chunk[4] = self.info.pixel_format.bits_per_pixel
                    chunk[5] = self.info.pixel_format.depth
                    chunk[6] = self.info.pixel_format.big_endian_flag
                    chunk[7] = self.info.pixel_format.true_colour_flag
                    chunk.writeUInt16BE(self.info.pixel_format.red_max, 8)
                    chunk.writeUInt16BE(self.info.pixel_format.green_max, 10)
                    chunk.writeUInt16BE(self.info.pixel_format.blue_max, 12)
                    chunk[14] = self.info.pixel_format.red_shift
                    chunk[15] = self.info.pixel_format.green_shift
                    chunk[16] = self.info.pixel_format.blue_shift
                    chunk[17] = 0 // padding
                    chunk[18] = 0 // padding
                    chunk[19] = 0 // padding
                    chunk.writeUInt32BE(self.info.name.length, 20)
                    chunk.write(self.info.name, 24, self.info.name.length)

                    self.socket.write(chunk);
                    self.mode = 4;

                    // Создаем свой локальный буфер
                    self.frameBuffer = Buffer.alloc(
                        self.info.width
                        * self.info.height
                        * self.info.pixel_format.bits_per_pixel / 8);

                    break;

                case 4:

                    // Принимаем данные о том, что может отобразить клиент. В данном случае это не важно
                    if (!self.buffer.isHas(20)) {
                        return;
                    }

                    // Игнорируем пиксельные предпочтения клиента, здесь это не нужно
                    self.buffer.get(20);
                    self.mode = 5;

                    break;

                case 5:

                    // Декодеры
                    if (self.encodings_count == undefined) {

                        if (!self.buffer.isHas(4)) {
                            return;
                        }

                        self.buffer.get(2).readInt16BE(0);

                        // Количество поддерживаемых декодеров
                        self.encodings_count = self.buffer.get(2).readInt16BE(0);

                    } else {

                        if (!self.buffer.isHas(4 * self.encodings_count)) {
                            return;
                        }

                        let zlib_exists = false;

                        // Список типов декодеров
                        for (var i = 0; i < self.encodings_count; i++) {
                            let enc_type = self.buffer.get(4).readInt32BE(0);
                            self.encodings.push(enc_type);
                            if (enc_type == 6) {
                                zlib_exists = true;
                            }
                        }

                        // Если не поддерживается zlib декодер, 
                        if (!zlib_exists) {
                            console.log('Error:not unavailable zlib decoder!!');
                            // Отключаемся
                            self.socket.end();
                        }

                        self.encodings_count = undefined;
                        self.mode = 6;
                    }

                    break;

                case 6:

                    // Рабочий поток
                    if (self.command == undefined) {
                        self.command = self.buffer.get(1).readUInt8(0);
                    }

                    self.mainThread();

                    break;
            }
        }
    });

    this.mainThread = function () {
        switch (self.command) {

            // SetPixelFormat
            case 0:
                if (!self.buffer.isHas(3 + 16)) {
                    return;
                }

                self.handleSetPixelFormat();
                break;

            // SetEncodings
            case 2:

                if (self.encodings_count == undefined) {
                    if (!self.buffer.isHas(1 + 2)) {
                        return;
                    }

                    self.buffer.get(1);

                    // Количество поддерживаемых декодеров
                    self.encodings_count = self.buffer.get(2).readInt16BE(0);
                } else {

                    if (!self.buffer.isHas(4 * self.encodings_count)) {
                        return;
                    }

                    self.handleSetEncodings();
                }
                break;

            // FrameBufferUpdateRequest
            case 3:
                if (!self.buffer.isHas(9)) {
                    return;
                }

                self.handleFramebufferUpdateRequest();
                self.command = undefined;
                break;

            // KeyEvent
            case 4:
                if (!self.buffer.isHas(7)) {
                    return;
                }

                self.handleKeyEvent();
                self.command = undefined;
                break;

            // PointerEvent
            case 5:
                if (!self.buffer.isHas(5)) {
                    return;
                }

                self.handlePointerEvent();
                self.command = undefined;
                break;

            // ClientCutText
            case 6:

                if (self.clipboard_length == undefined) {

                    if (!self.buffer.isHas(7)) {
                        return;
                    }

                    self.buffer.get(3);
                    self.clipboard_length = self.buffer.get(4).writeInt32BE(0);

                } else {

                    if (!self.buffer.isHas(self.clipboard_length)) {
                        return;
                    }

                    self.handleReceiveClipboardData();
                    self.command = undefined;
                    self.clipboard_length = undefined;
                }

                break;
        }
    }

    this.handleFramebufferUpdateRequest = function () {
        let incremental = self.buffer.get(1).readUInt8(0);
        let rect = {};
        rect.x = self.buffer.get(2).readUInt16BE(0);
        rect.y = self.buffer.get(2).readUInt16BE(0);
        rect.width = self.buffer.get(2).readUInt16BE(0);
        rect.height = self.buffer.get(2).readUInt16BE(0);

        console.log("rect x=" + rect.x + " y=" + rect.y + " w=" + rect.width + " h=" + rect.height + " inc=" + incremental);

        self.respondToUpdateRequest(incremental, rect);
    }

    this.handleSetPixelFormat = function () {
        self.buffer.get(19);
    }

    this.handleSetEncodings = function () {
        let zlib_exists = false;
        self.encodings = [];

        // Список типов декодеров
        for (var i = 0; i < self.encodings_count; i++) {
            let enc_type = self.buffer.get(4).readInt32BE(0);
            self.encodings.push(enc_type);
            if (enc_type == 6) {
                zlib_exists = true;
            }
        }

        // Если не поддерживается zlib декодер, 
        if (!zlib_exists) {
            console.log('Error:not unavailable zlib decoder!!');
            // Отключаемся
            self.socket.end();
        }

        self.encodings_count = undefined;
    }

    this.handleKeyEvent = function () {
        self.buffer.get(7);
        /*var pressed = this.c.ReceiveByte() != 0;
        this.c.Receive(2);
        var keysym = (KeySym)this.c.ReceiveUInt32BE();

        this.OnKeyChanged(new KeyChangedEventArgs(keysym, pressed));*/
    }

    this.handlePointerEvent = function () {
        self.buffer.get(5);
        /*int pressedButtons = this.c.ReceiveByte();
        int x = this.c.ReceiveUInt16BE();
        int y = this.c.ReceiveUInt16BE();

        this.OnPointerChanged(new PointerChangedEventArgs(x, y, pressedButtons));*/
    }

    this.handleReceiveClipboardData = function () {
        self.buffer.get(self.clipboard_length);
    }
}

module.exports = VncServerSession;