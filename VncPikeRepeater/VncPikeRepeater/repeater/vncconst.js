module.exports.SecurityType = {
	Invalid: 0,
	None: 1,
	VNC: 2
};

module.exports.ServerMessage = {
	FramebufferUpdate: 0,
	SetColorMapEntries: 1,
	Bell: 2,
	ServerCutText: 3
};

module.exports.ClientMessage = {
	SetPixelFormat: 0,
	SetEncodings: 2,
	FramebufferUpdateRequest: 3,
	KeyEvent: 4,
	PonterEvent: 5,
	ClientCutText: 6
};

module.exports.MessageType = {
	ProtocolVersionHandshake: 0,
	SecurityHandshake33: 1,
	SecurityHandshake: 2,
	VNCAuthentication: 3,
	SecurityResultHandshake: 4,
	ClientInit: 5,
	ServerInit: 6,

	FramebufferUpdate: 100,
	SetColorMapEntries: 101,
	Bell: 102,
	ServerCutText: 103,

	SetPixelFormat: 200,
	SetEncodings: 201,
	FramebufferUpdateRequest: 202,
	KeyEvent: 203,
	PonterEvent: 204,
	ClientCutText: 205,
};
