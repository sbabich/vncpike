var net = require('net');
var BufferQueue = require('../repeater/BufferQueue.js');
var EventEmitter = require('events').EventEmitter;
var zlib = require('zlib');

var rfb = function () {

    this.events = new EventEmitter();

    //TCP/IP
    this.client = new net.Socket();
    this.client.setTimeout(180000);

    this.mode = 0;
    this.work_stage = -1;
    this.buffer = new BufferQueue(10 * 1024 * 1024);
    this.auth_mode_count = undefined;
    this.padding = undefined;
    this.namesize = undefined;
    this.lookerPassword = "";
    this.frameBuffer = undefined;

    //Server data
    this.info =
    {
        name:'',
        ver:0,
        width:0,
        height:0,
        pixel_format:
        {
            bits_per_pixel:0,
            depth:0,
            big_endian_flag:0,
            true_colour_flag:0,
            red_max:0,
            green_max:0,
            blue_max:0,
            red_shift:0,
            green_shift:0,
            blue_shift:0,
        }
    };

    this.BufList = [];

    this.rect = {};
};

rfb.prototype.connect = function(ipadress,port_number,password){
    var self = this;

    self.client.connect({host:ipadress,port:port_number}, function(){
        console.log('client -> Connect to vncserver');
    });
    self.client.on('connect', function(){
        console.log('client -> Connected');
        self.client.setTimeout(0);
        self.client.setNoDelay(true);
    });
    self.client.on('data', function (data) {

        // Укладывем данные в буфер
        self.buffer.append(data, 0, data.length); 

        while (self.buffer.isHas(1)) {

            switch (self.mode) {
                case 0:
                    // Обмениваемся версиями
                    if (!self.buffer.isHas(12)) {
                        return;
                    }

                    let version = self.buffer.get(12).toString();
                    console.log(version);

                    if (version == 'RFB 003.008\n') {
                        self.info.ver = 3.8;
                        self.mode = 1;
                        self.client.write('RFB 003.008\n');
                    } else {
                        console.log('Error:not mach protocol vertion');
                        self.client.end();
                    }
                    break;

                case 1:
                    // Прием методов аутентификации
                    if (self.auth_mode_count == undefined) {

                        if (!self.buffer.isHas(1)) {
                            return;
                        }

                        self.auth_mode_count = self.buffer.get(1).readUInt8(0);

                    } else {

                        if (self.auth_mode_count <= 0) {
                            self.mode = -1;
                            return;
                        }

                        if (!self.buffer.isHas(self.auth_mode_count)) {
                            return;
                        }

                        for (var i = 1; i <= self.auth_mode_count; i++) {
                            if (self.buffer.get(1).readUInt8(0) == 2) {

                                // Если среди них есть пароль, то ок, выбираем его
                                self.client.write('\u0002');
                                self.mode = 2;
                            }
                        }

                        if (self.mode != 2) {
                            console.log("Cant choose auth mode!");
                            self.mode = -1;
                            return;
                        }
                    }

                    break;

                case 2:
                    // Ответ на пароль
                    //var response = des.MakeResponse(password,data);
                    self.buffer.get(16);
                    let buff = new Uint8Array(16);
                    self.client.write(buff);
                    self.mode = 3;
                    break;

                case 3:
                    if (!self.buffer.isHas(4)) {
                        return;
                    }

                    // Сервер принял пароль
                    if (self.buffer.get(4).readInt32BE(0) == 0) {
                        self.mode = 4;
                        self.client.write('\u0001');
                    } else {
                        console.log('Error:failure authentication');
                    }
                    break;

                case 4:
                    // Инфа о сервере
                    if (!self.buffer.isHas(17)) {
                        return;
                    }
                    self.ReceiveVncServerInfo();
                    self.mode = 5;
                    break;

                case 5:
                    //Обмен данными об изображении
                    if (self.padding == undefined) {

                        if (!self.buffer.isHas(3)) {
                            return;
                        }

                        self.padding = 0;
                        self.buffer.get(3);
                    }

                    if (self.namesize == undefined) {

                        if (!self.buffer.isHas(4)) {
                            return;
                        }

                        self.namesize = self.buffer.get(4).readInt32BE(0);
                    }

                    if (!self.buffer.isHas(self.namesize)) {
                        return;
                    }

                    self.ReceiveVncServerNameAndLookerPassword();

                    self.info.pixel_format.bits_per_pixel = 32;
                    self.info.pixel_format.depth = 8;
                    self.info.pixel_format.big_endian_flag = 0;
                    self.info.pixel_format.true_colour_flag = 1;

                    // Формат
                    self.SetPixelFormat();

                    // Обмен способов декодирования
                    self.SetEncodings();

                    // Запуск потока опроса
                    self.events.emit('connected', self.info);

                    self.mode = 6;
                    break;

                case 6:
                    // Рабочий поток
                    while (self.buffer.getCount() > 0) {

                        //Если не определен stage - то читаем его
                        if (this.work_stage == undefined) {

                            if (!self.buffer.isHas(2)) {
                                return;
                            }
                            this.work_stage = self.buffer.get(2).readUInt16BE(0);
                            continue;

                        } else {
                            switch (this.work_stage) {

                                // FramebufferUpdate принимаем прямоугольники
                                case 0:

                                    //Принимаем кол-во прямоугольников
                                    if (self.rect.number_of_rectangles == undefined) {

                                        if (!self.buffer.isHas(2)) {
                                            return;
                                        }

                                        self.rect.number_of_rectangles = self.buffer.get(2).readUInt16BE(0);
                                        self.BufferSize = undefined;
                                        //self.BufList = [];
                                        //data = data.slice(2);

                                        console.log('New rectagles = ' + self.rect.number_of_rectangles);

                                        continue;

                                    } else {

                                        //var buf = data;
                                        //Выбираем прямоугольники по одному
                                        if (self.BufferSize == undefined) {

                                            if (!self.buffer.isHas(16)) {
                                                return;
                                            }

                                            self.rect.x_position = self.buffer.get(2).readUInt16BE(0);
                                            self.rect.y_position = self.buffer.get(2).readUInt16BE(0);
                                            self.rect.width = self.buffer.get(2).readUInt16BE(0);
                                            self.rect.height = self.buffer.get(2).readUInt16BE(0);
                                            self.rect.encoding_type = self.buffer.get(4).readInt32BE(0);
                                            self.rect.content_length = self.buffer.get(4).readInt32BE(0);

                                            self.BufferSize = 0;
                                        }

                                        if (!self.buffer.isHas(self.rect.content_length)) {
                                            return;
                                        }

                                        let uncompressedSize = self.rect.width * self.rect.height * 4;
                                        let frame_buffer = self.buffer.get(self.rect.content_length);

                                        if (self.rect.encoding_type == 6) {
                                            frame_buffer = zlib.inflateSync(frame_buffer);
                                            if (frame_buffer.length != uncompressedSize) {
                                                throw new Error("Incomplete zlib block");
                                            }
                                        }

                                        self.rect.content = new Buffer(frame_buffer);
                                        self.insertRect(self.rect);
                                        //self.BufList.push(frame_buffer);
                                        //self.events.emit('encode');
                                        //self.BufList = []; // temporary

                                        console.log(self.rect.number_of_rectangles
                                            + ') rect: x='
                                            + self.rect.x_position
                                            + ' y='
                                            + self.rect.y_position
                                            + " size="
                                            + self.rect.width
                                            + 'x'
                                            + self.rect.height
                                            + " size="
                                            + self.rect.content_length);

                                        self.BufLis = [];
                                        self.BufferSize = undefined;
                                        self.rect.number_of_rectangles--;

                                        self.rect.x_position = undefined;
                                        self.rect.y_position = undefined;
                                        self.rect.width = undefined;
                                        self.rect.height = undefined;
                                        self.rect.encoding_type = undefined;
                                        self.rect.content_length = undefined;

                                        //Если прямоугольники закончились то опять ждем stage
                                        if (self.rect.number_of_rectangles == 0) {
                                            this.work_stage = undefined;
                                            self.rect.number_of_rectangles = undefined;
                                        }
                                    }

                                    break;

                                case 1:
                                    self.mode = 7;
                                    console.log('SetColourMapEntries');
                                    //console.log(data);
                                    self.rect.first_colour = data.slice(3, 5).readUInt16BE(0);
                                    self.rect.number_of_colours = data.slice(5, 7).readUInt16BE(0);
                                    self.BufList = [];
                                    self.BufferSize = 0;
                                    self.client.setTimeout(100);
                                    break;
                                case 2:
                                    console.log("Bell!");
                                    break;
                                case 3:
                                    console.log("ServerCutText");
                                    break;
                            }
                        }
                    }
                    break;

                case -1:
                    console('Error:' + data.slice(4).toString());
                    self.client.end();
                    break;
            }
        }
    });
    
    self.events.on('encode', function(){
        self.mode = 5;
        var buf = Buffer.concat(self.BufList, self.BufList.totalLength);
        //console.log(buf);
        self.rect.data = new Buffer(self.rect.width*self.rect.height*3);
        for(var i=0;i<self.rect.width*self.rect.height;i++){
            self.rect.data[3*i  ] = buf[4*i+2];
            self.rect.data[3*i+1] = buf[4*i+1];
            self.rect.data[3*i+2] = buf[4*i];
        }
        self.events.emit('data',self.rect);
    });
    
    self.client.on('error', function(data){
        console.log(''+data);
    });
    self.client.on('end', function(){
        console.log('end');
    });
    self.client.on('close', function(){
        console.log('client-> connection is closed');
    });
    self.client.on('timeout',function(){
        console.log('timeout');
        switch (self.mode) {
            case 7:
                self.mode = 5;
                self.rect.buf = Buffer.concat(self.BufList, self.BufList.totalLength);
                break;
            default:
                console.log('client -> Timeout');
                self.client.end();
        }
    });
    
};

rfb.prototype.ReceiveVncServerInfo = function() {

    this.info.width = this.buffer.get(2).readUInt16BE(0);
    this.info.height = this.buffer.get(2).readUInt16BE(0);

    this.info.pixel_format.bits_per_pixel = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.depth = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.big_endian_flag = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.true_colour_flag = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.red_max = this.buffer.get(2).readUInt16BE(0);
    this.info.pixel_format.green_max = this.buffer.get(2).readUInt16BE(0);
    this.info.pixel_format.blue_max = this.buffer.get(2).readUInt16BE(0);
    this.info.pixel_format.red_shift = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.green_shift = this.buffer.get(1).readUInt8(0);
    this.info.pixel_format.blue_shift = this.buffer.get(1).readUInt8(0);

    this.frameBuffer = Buffer.alloc(
        this.info.width
        * this.info.height
        * this.info.pixel_format.bits_per_pixel / 8);
}

rfb.prototype.insertRect = function (rect) {

    let rect_x = rect.x_position;
    let rect_y = rect.y_position;
    let rect_sw = rect.width;
    let rect_sh = rect.height;

    let frame_sx = 0;
    let frame_sy = 0;
    let frame_tw = this.info.width;
    let frame_th = this.info.height;

    let bpp = this.info.pixel_format.bits_per_pixel / 8;

    for (var iy = 0; iy < rect.height; iy++) {

        let sourceStart = bpp * (((iy + frame_sy) * rect_sw) + frame_sx);
        let sourceEnd = bpp * rect_sw;
        let targetStart = bpp * (((iy + rect_y) * frame_tw) + rect_x);

        //buffer.copy(target, targetStart, sourceStart, sourceEnd);
        rect.content.copy(this.frameBuffer, targetStart, sourceStart, sourceEnd);
    }
}

rfb.prototype.ReceiveVncServerNameAndLookerPassword = function () {
    //Padding  3byte.17-20
    //NameSize 4byte.20-24 
    var name_and_password = this.buffer.get(this.namesize).toString();

    //this.info.name = name_and_password;

    let arr = name_and_password.split("\\r\\n");
    if (arr.length >= 1) {
        this.info.name = arr[0];
    }
    if (arr.length >= 2) {
        this.lookerPassword = arr[1];
    }
}

rfb.prototype.SetPixelFormat = function(){
    var buf = new Buffer(20);
    buf.fill(0);

    buf.writeUInt8(0,0);//message-type
    
    buf.writeUInt8(0,1);//padding
    buf.writeUInt8(0,2);
    buf.writeUInt8(0,3);
    
    buf.writeUInt8(this.info.pixel_format.bits_per_pixel,4);  //bits_per_pixel
    buf.writeUInt8(this.info.pixel_format.depth,5);           //depth
    buf.writeUInt8(this.info.pixel_format.big_endian_flag,6); //big_endian_flag    
    buf.writeUInt8(this.info.pixel_format.true_colour_flag,7);//true_colour_flag,
    
    buf.writeUInt16BE(this.info.pixel_format.red_max,8);   //red_max
    buf.writeUInt16BE(this.info.pixel_format.green_max,10);//green_max
    buf.writeUInt16BE(this.info.pixel_format.blue_max,12); //blue_max
    
    buf.writeUInt8(this.info.pixel_format.red_shift,14);  //red_shift
    buf.writeUInt8(this.info.pixel_format.green_shift,15);//green_shift
    buf.writeUInt8(this.info.pixel_format.blue_shift,16); //blue_shift              
    
    buf.writeUInt8(0x0,17);//padding
    buf.writeUInt8(0x0,18);
    buf.writeUInt8(0x0,19);
    
    //console.log(buf);
    this.client.write(buf);
}

rfb.prototype.SetEncodings = function(){
    var buf = new Buffer(12);
    
    buf.writeUInt8(2,0);    //message-type
    
    buf.writeUInt8(0,1);    //padding
    
    buf.writeUInt16BE(2, 2); //number-of-encodings
    buf.writeInt32BE(0, 4);  //encoding-type RAW   
    buf.writeInt32BE(6, 4);  //encoding-type ZLIB 
    
    //console.log(buf);
    this.client.write(buf);  
}

rfb.prototype.FramebufferUpdateRequest = function(i,x,y,w,h){
    var buf = new Buffer(10);
    
    buf.writeUInt8(3,0);   //message-type
    
    buf.writeUInt8(i,1);   //incremental
    
    buf.writeUInt16BE(x,2);//x-position
    buf.writeUInt16BE(y,4);//y-position
    buf.writeUInt16BE(w,6);//width
    buf.writeUInt16BE(h,8);//height
   
    //console.log(buf);
    this.client.write(buf);
}

rfb.prototype.KeyEvent = function(down,key){
    if (key > 0) {
        var buf = new Buffer(8);
        buf.writeUInt8(4,0);   //message-type
        buf.writeUInt8(down,1);//down-flag  
        buf.writeUInt16BE(0,3);//padding
        buf.writeUInt32BE(key,4);//key
        console.log(key+":",buf);
        this.client.write(buf);
    }
}

rfb.prototype.PointerEvent = function(mask,x,y){
    var buf = new Buffer(6);
    buf.writeUInt8(5,0);   //message-type
    
    switch (mask) {
        case 0:
            buf.writeUInt8(1 << 0,1);
            break;
        case 1:
            buf.writeUInt8(1 << 1,1);
            break;
        case 2:
            buf.writeUInt8(1 << 2,1);
            break;
        case 3:
            buf.writeUInt8(1 << 3,1);
            break;
        case 4:
            buf.writeUInt8(1 << 4,1);
            break;
        case 5:
            buf.writeUInt8(1 << 5,1);
            break;
        default:
            buf.writeUInt8(0,1);
            break;
    }
    
    buf.writeUInt16BE(x,2);//x-position
    buf.writeUInt16BE(y,4);//y-position
    console.log(buf);
    this.client.write(buf);
}

module.exports = rfb;