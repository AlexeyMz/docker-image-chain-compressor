using System;
using System.Collections;
using System.IO;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Tar;

namespace Unboxer
{
    public class imgToCollect
    {
        private ArrayList cons = new ArrayList();

        public imgToCollect(FileStream fileInput)
        {
            Directory.CreateDirectory("TarOutPut");
            TarInputStream tarInputStream = new TarInputStream(fileInput);
            TarEntry TarFromFile = tarInputStream.GetNextEntry();

            while (TarFromFile != null)
            {
                if (TarFromFile.IsDirectory)
                {
                    String name = (TarFromFile.Name);
                    cons.Add(name);
                    Directory.CreateDirectory("TarOutPut/" + name);
                }
                else
                {
                    FileInfo fInfo = new FileInfo(string.Format("TarOutPut/"+ TarFromFile.Name));
                    FileStream file = fInfo.Create();
                    tarInputStream.CopyTo(file);
                    file.Close();   
                }
                TarFromFile = tarInputStream.GetNextEntry();
            }


            Console.WriteLine ("readed");
        }

        public void compose()
        {

        }
    }
}

