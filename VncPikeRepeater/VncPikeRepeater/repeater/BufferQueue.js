function BufferQueue(size) {
    this._buff = new Uint8Array(size); 
    this._size = size;
    this._head = 0;
    this._tail = 0;
    this._count = 0;

    this.getCount = function () {

        return this._count;
    }

    this.isHas = function (len) {

        return this._count >= len;
    }

    this.getSize = function () {

        return this._size;
    }

    this.append = function (data, offset, length)
    {
        if (data == null) {
            return;
        }

        if (data.length < offset + length) {
            throw new Exception("array index out of bounds. offset + length extends beyond the length of the array.");
        }

        for (i = 0; i < length; i++) {
            this._buff[(i + this._tail) % this._size] = data[i + offset];
        }

        this._tail = (length + this._tail) % this._size;
        // We need to acquire the mutex so that this.count doesn't change.
        this._count = this._count + length;
        if (this._count > this._size)
            throw new Exception("Buffer overflow error.");
    }

    this.read = function (data, offset, length)
    {
        if (data == null)
            return 0;

        if (data.length < offset + length) {
            throw new Exception("array index out of bounds. offset + length extends beyond the length of the array.");
        }

        let readlength = 0;

        for (i = 0; i < length; i++)
        {
            if (i == this._count)
                break;

            data[i + offset] = this._buff[(i + this._head) % this._size];
            readlength++;
        }
        this._head = (readlength + this._head) % this._size;

        this._count = this._count - readlength;
        return readlength;
    }

    this.get = function (length) {
        var data = new Uint8Array(length);
        let len = this.read(data, 0, length);

        return new Buffer(data);
    }
}

module.exports = BufferQueue;