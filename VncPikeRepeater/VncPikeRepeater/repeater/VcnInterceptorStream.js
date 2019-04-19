var BufferQueue = require("BufferQueue");
var Stream = require('readable-stream');
var util = require('util');

util.inherits(VcnInterceptorStream, Stream.Writable);
util.inherits(VcnInterceptorStream, Stream.Readable);

function VcnInterceptorStream(_serverSocket) {
    this._bq = new BufferQueue(1024 * 1024 * 10);

    this.write = function (chunk, encoding, callback) {
        var ret = Stream.Writable.prototype.write.apply(this, arguments);
        if (!ret) {
            this.emit('drain');
        }
        return ret;
    }

    this._write = function (chunk, encoding, callback) {
        this.write(chunk, encoding, callback);
    };

    this._read = function (n) {
        this.push(this._data);
        this._data = '';
    };

    this.append = function (data) {
        this.push(data);
    };

    this.end = function (chunk, encoding, callback) {
        var ret = Stream.Writable.prototype.end.apply(this, arguments);
        // In memory stream doesn't need to flush anything so emit `finish` right away
        // base implementation in Stream.Writable doesn't emit finish
        this.emit('finish');
        return ret;
    };
}

module.exports = VcnInterceptorStream;