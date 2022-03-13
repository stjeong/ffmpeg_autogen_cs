using System;
using System.IO;
using System.Security.Cryptography;

namespace ffhash
{
    internal unsafe class Program
    {
        static void Main(string[] args)
        {
            string dirPath = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? "";
            string src_filename = Path.Combine(dirPath, "..", "..", "..", "Samples", "sample-10s.mp4");

            using (FileStream fs = File.OpenRead(src_filename))
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(fs);
                Console.WriteLine(BitConverter.ToString(hash).Replace("-", ""));
            }
        }
    }
}

/*
BE998E5FED5A9065BBCCDB097884794EBD2492CB261EC40948E7A846AF5724B8
*/