from . import _network_manager


__all__ = ["Configure", "Connect"]

def Configure(port=12345):
	_network_manager.Configure(port)

def Configure():
	_network_manager.Configure()

def Connect(address, port, username, password):
	_network_manager.Connect(address, port, username, password)



