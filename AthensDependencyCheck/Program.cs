﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AthensDependencyCheck
{
    class Program
    {
        static void GenerateTables()
        {
            var insertDll = "INSERT INTO DLL VALUES('{0}', {1});";
            var insertFunction = "INSERT INTO FUNCTION VALUES('{0}', '{1}');";

            SQLiteConnection.CreateFile("athensCheck.db3");
            using (var connection = new SQLiteConnection("data source=athensCheck.db3"))
            {
                connection.Open();

                using (var command = new SQLiteCommand(connection))
                {
                    // Create the DLL Table
                    command.CommandText = @"CREATE TABLE IF NOT EXISTS DLL 
                                            (
                                                D_NAME VARCHAR(255) NOT NULL PRIMARY KEY,
                                                D_IS_UAP INTEGER NOT NULL CHECK(D_IS_UAP IN (0, 1))
                                            );";
                    command.ExecuteNonQuery();

                    // Create the Function Table
                    command.CommandText = @"CREATE TABLE IF NOT EXISTS FUNCTION 
                                            (
                                                F_NAME VARCHAR(255) NOT NULL PRIMARY KEY,
                                                F_DLL_NAME INTEGER NOT NULL,
                                                FOREIGN KEY(F_DLL_NAME) REFERENCES DLL(D_NAME)
                                            );";
                    command.ExecuteNonQuery();

                    // Ensure we wiped the old tables
                    command.CommandText = "DELETE FROM DLL;";
                    command.ExecuteNonQuery();
                    command.CommandText = "DELETE FROM FUNCTION;";
                    command.ExecuteNonQuery();

                    try
                    {
                        using (var reader = new StreamReader("dlls.txt"))
                        {
                            while (!reader.EndOfStream)
                            {
                                // Get the DLL name and whether it is available in a UAP app
                                var dllName = reader.ReadLine().ToLower().Trim();
                                var isUAP = Convert.ToInt32(reader.ReadLine().Trim().Equals("UAP"));

                                command.CommandText = string.Format(insertDll, dllName, isUAP);
                                command.ExecuteNonQuery();

                                // Get the functions for that DLL
                                while (!reader.EndOfStream)
                                {
                                    var functionName = reader.ReadLine();

                                    // Terminate on end of file or blank line
                                    if (string.IsNullOrWhiteSpace(functionName))
                                    {
                                        break;
                                    }

                                    command.CommandText = string.Format(insertFunction, functionName, dllName);
                                    command.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                    catch (IOException)
                    {
                        Console.Error.WriteLine("Error Reading Input File");
                        Environment.Exit(1);
                    }
                }
            }
        }

        static string[] GetDumpbinOutput(string dllName)
        {
            // Create a temporary file to store the dumpbin output
            var temp = Path.GetTempFileName();

            // Create a batch file to store the dumpbin command
            var bat = Path.ChangeExtension(temp, ".bat");
            File.WriteAllLines(bat, new[] {
                @"call ""c:\Program Files (x86)\Microsoft Visual Studio 11.0\Common7\Tools\VsDevCmd.bat""",
                "dumpbin.exe /imports " + dllName + " >> " + temp
            });

            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C \"" + bat + "\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(info).WaitForExit();

            var output = File.ReadLines(temp).ToArray();

            // Cleanup
            File.Delete(temp);
            File.Delete(bat);

            return output;
        }

        static void ProcessLines(string[] lines, bool isUAP)
        {
            // Queries
            var selectDll = "SELECT * FROM DLL WHERE D_NAME = '{0}' AND D_IS_UAP = {1};";
            var functionSelect = "SELECT * FROM FUNCTION WHERE F_NAME = '{0}';";
            var functionSelectWithDll = functionSelect + "AND F_DLL_NAME = '{1}';";
            var functionDLLJoin = "SELECT * FROM FUNCTION, DLL WHERE F_NAME = '{0}' AND D_NAME = F_DLL_NAME";

            // Output formatting
            var dllOutput = "{0}\t\tInAthens={1}";
            var functionOutput = "\t{0}\t\tInAthens={1}";
            var alternateDllFunctionOutput = "\t{0}\t\tInAthens={1}\t\tFound in {2} instead";

            // Counts for errors/warnings
            var invalidDllCount = 0;
            var invalidFunctionCount = 0;
            var differentDllFunctionCount = 0;

            using (var connection = new SQLiteConnection(@"data source=athensCheck.db3"))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    // Start at line 10 to eliminate pre-import output
                    var index = 10;

                    // Parse the dlls
                    while (true)
                    {
                        var currentLine = lines[index].ToLower().Trim();

                        // We hit a different section of imports, skip to the dlls
                        if (currentLine.StartsWith("section"))
                        {
                            index += 2;
                            currentLine = lines[index].ToLower().Trim();
                        }

                        // If we run out of dlls we are done
                        if (!currentLine.EndsWith(".dll"))
                        {
                            break;
                        }

                        var dllName = currentLine;

                        // Try to get the dll from the db
                        command.CommandText = string.Format(selectDll, dllName, Convert.ToInt32(isUAP));
                        var dataReader = command.ExecuteReader();
                        var dllExists = dataReader.Read();

                        if (!dllExists)
                        {
                            invalidDllCount++;
                        }

                        dataReader.Close();

                        Console.Out.WriteLine(string.Format(dllOutput, dllName, dllExists));

                        // Loop through the preamble (of variable length)
                        while (!string.IsNullOrWhiteSpace(currentLine))
                        {
                            index++;
                            currentLine = lines[index].ToLower().Trim();
                        }

                        // Parse each function inside the dll
                        while (true)
                        {
                            index++;
                            currentLine = lines[index].Trim();

                            // If we hit a line of whitespace move onto the next dll
                            if (string.IsNullOrWhiteSpace(currentLine))
                            {
                                index++;
                                break;
                            }

                            var tableComponents = currentLine.Split(' ');

                            if (tableComponents[0] == "Ordinal")
                            {
                                continue;
                            }

                            var functionName = tableComponents.Last();

                            // If the dll exists double check that the function exists, otherwise see if there is another dll that has it
                            if (dllExists)
                            {
                                command.CommandText = string.Format(functionSelectWithDll, functionName, dllName);
                                dataReader = command.ExecuteReader();
                                var functionExists = dataReader.Read();

                                if (!functionExists)
                                {
                                    invalidFunctionCount++;
                                }

                                dataReader.Close();

                                Console.Out.Write(string.Format(functionOutput, functionName, functionExists));
                            }
                            else
                            {
                                command.CommandText = string.Format(functionDLLJoin, functionName);
                                dataReader = command.ExecuteReader();
                                var functionExists = dataReader.Read();

                                // Ensure that the dll is UAP/Non-UAP compatible and that the function exists
                                if (functionExists && Convert.ToBoolean(dataReader["D_IS_UAP"]) == isUAP)
                                {
                                    Console.Out.WriteLine(alternateDllFunctionOutput, functionName, functionExists, dataReader["D_NAME"]);

                                    differentDllFunctionCount++;
                                }
                                else
                                {
                                    invalidFunctionCount++;
                                    Console.Out.WriteLine(functionOutput, functionName, functionExists);
                                }

                                dataReader.Close();
                            }
                        }
                    }
                }
            }

            // Summary output
            Console.Out.WriteLine("\n\nSummary");
            Console.Out.WriteLine(invalidDllCount == 0 ? "All DLLs are compatible" : "Number of DLLs incompatible: " + invalidDllCount);
            Console.Out.WriteLine(invalidFunctionCount == 0 ? "All functions are comptible" : "Number of functions incompatible: " + invalidFunctionCount);
            if (differentDllFunctionCount > 0) {
                Console.Out.WriteLine("Number of functions found in different DLLs: " + differentDllFunctionCount);
            }

            if (invalidDllCount + invalidFunctionCount + differentDllFunctionCount == 0)
            {
                Console.Out.WriteLine("\n\nYour DLL is compatible with Windows Athens!");
            }
        }

        static void InvalidUsage()
        {
            Console.Error.WriteLine("Usage: AthensDependencyCheck.exe (generate | [dllName].dll [-u])");
            Environment.Exit(1);
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2)
            {
                InvalidUsage();
            }

            if (args.Length == 1 && args[0].Equals("generate"))
            {
                GenerateTables();
                return;
            }

            if (!args[0].ToLower().EndsWith(".dll"))
            {
                InvalidUsage();
            }

            var isUAP = true;

            if (args.Length == 2) {
                if (!args[1].Equals("-u"))
                {
                    InvalidUsage();
                } 
                else 
                {
                    isUAP = false;
                }
            }

            var lines = GetDumpbinOutput(args[0]);
            ProcessLines(lines, isUAP);
        }
    }
}
