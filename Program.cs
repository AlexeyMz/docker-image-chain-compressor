using System;
using System.Collections;
using System.IO;
using ICSharpCode.SharpZipLib;

namespace Unboxer
{
	class MainClass
	{
		public static void Main (string[] args)
		{
            FileStream file = new FileStream("/home/waller/Unboxer/Unboxer/python.tar", FileMode.Open, FileAccess.Read);
            imgToCollect tt = new imgToCollect(file);
			Console.WriteLine ("Hello World!");
		}
	}
}
