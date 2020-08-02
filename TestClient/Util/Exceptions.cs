using Microsoft.Scripting.Hosting;
using System;
using TestClient.Scripts;

namespace TestClient.Util
{
	internal static class Exceptions
	{
		public static void PythonException(this Exception ex, uint? messageType = null)
		{
			ExceptionOperations eo = ScriptManager.GetEngine().GetService<ExceptionOperations>();
			string error = eo.FormatException(ex);
			if (messageType != null)
			{
				WriteChat($"Error handling message {messageType:X4} : {error}");
			}
			else
			{
				WriteChat(error);
			}
		}
		public static void WriteChat(string chat)
		{
			Console.WriteLine(chat);
		}
	}
}
