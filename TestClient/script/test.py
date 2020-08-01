from __future__ import print_function
#from builtins import range

import api
from api.commands import *
from api.messages import *
from api.network import *
from api.display import ChatPane, enablePane
from api.character import *

chat = ChatPane()
enablePane(chat)

def globalChatMessage(msg):
	if msg.type == 1:
		# channel, senderName, text
		chat.addChat("({:4x}) {:>25}: {}".format(msg.channel, msg.senderName, msg.text))

HandleMessage(0xf7de, globalChatMessage)




def createObject(msg):
	#print("Create Object", msg.Value[int]("object"))
	phys = msg.child("physics")
	flags = phys.flags
	if flags & 0x00030000:
		print(msg.child("game").name)

def characterInfo(msg):
	print("Character Info")
	#vecs = msg.child("vectors")

HandleMessage(0xf745, createObject)

HandleMessageEvent(0xf7b0, 0x0013, characterInfo)

#called explicitly on program startup, by the script state machine, and when start command is used
def Startup():
	#print("Startup")
	Configure()
	Connect("127.0.0.1", 9000, "fart", "vSVZ3tLwSyFR7ZUp")
	#Login(0)

class TestMachine(object):
	def __init__(self):
		self.numloops = 0
	def __call__(self):
		self.numloops = self.numloops + 1
		#print(self.numloops);
		#if (self.numloops % 10 == 0):
		#	Startup();


#state machine instance called explicitly every script thread itteration
Test = TestMachine()
	

