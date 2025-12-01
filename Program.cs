using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using FirebirdSql.Data.FirebirdClient;
using System.Text.Json;

namespace DbMetaTool
{
    public static class Program
    {
        // Przykładowe wywołania:
        // DbMetaTool build-db --db-dir "C:\db\fb5" --scripts-dir "C:\scripts"
        // DbMetaTool export-scripts --connection-string "..." --output-dir "C:\out"
        // DbMetaTool update-db --connection-string "..." --scripts-dir "C:\scripts"
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Użycie:");
                Console.WriteLine("  build-db --db-dir <ścieżka> --scripts-dir <ścieżka>");
                Console.WriteLine("  export-scripts --connection-string <connStr> --output-dir <ścieżka>");
                Console.WriteLine("  update-db --connection-string <connStr> --scripts-dir <ścieżka>");
                return 1;
            }

            try
            {
                var command = args[0].ToLowerInvariant();

                switch (command)
                {
                    case "build-db":
                        {
                            string dbDir = GetArgValue(args, "--db-dir");
                            string scriptsDir = GetArgValue(args, "--scripts-dir");

                            BuildDatabase(dbDir, scriptsDir);
                            Console.WriteLine("Baza danych została zbudowana pomyślnie.");
                            return 0;
                        }

                    case "export-scripts":
                        {
                            string connStr = GetArgValue(args, "--connection-string");
                            string outputDir = GetArgValue(args, "--output-dir");

                            ExportScripts(connStr, outputDir);
                            Console.WriteLine("Skrypty zostały wyeksportowane pomyślnie.");
                            return 0;
                        }

                    case "update-db":
                        {
                            string connStr = GetArgValue(args, "--connection-string");
                            string scriptsDir = GetArgValue(args, "--scripts-dir");

                            UpdateDatabase(connStr, scriptsDir);
                            Console.WriteLine("Baza danych została zaktualizowana pomyślnie.");
                            return 0;
                        }

                    default:
                        Console.WriteLine($"Nieznane polecenie: {command}");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Błąd: " + ex.Message);
                return -1;
            }
        }

        private static string GetArgValue(string[] args, string name)
        {
            int idx = Array.IndexOf(args, name);
            if (idx == -1 || idx + 1 >= args.Length)
                throw new ArgumentException($"Brak wymaganego parametru {name}");
            return args[idx + 1];
        }

        /// <summary>
        /// Buduje nową bazę danych Firebird 5.0 na podstawie skryptów.
        /// </summary>
        public static void BuildDatabase(string databaseDirectory, string scriptsDirectory)
        {
            if (!Directory.Exists(databaseDirectory))
                Directory.CreateDirectory(databaseDirectory);

            if (!Directory.Exists(scriptsDirectory))
                throw new DirectoryNotFoundException($"Katalog skryptów nie istnieje: {scriptsDirectory}");

            string dbPath = Path.Combine(databaseDirectory, "database.fdb");
            
            // Usuń bazę jeśli istnieje
            if (File.Exists(dbPath))
                File.Delete(dbPath);

            Console.WriteLine("Tworzenie nowej bazy danych...");
            
            // Utwórz bazę używając FbConnection.CreateDatabase z poprawnym connection stringiem
            string createConnectionString = $"User=SYSDBA;Password=masterkey;Database={dbPath};DataSource=localhost;Charset=UTF8;";
            try
            {
                FbConnection.CreateDatabase(createConnectionString, pageSize: 8192, forcedWrites: true, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Błąd podczas tworzenia bazy: {ex.Message}");
                Console.WriteLine("Próbuję alternatywną metodę...");
                
                // Alternatywna metoda - użyj isql do utworzenia bazy
                CreateDatabaseWithIsql(dbPath);
            }

            // Połącz się z nowo utworzoną bazą i wykonaj skrypty
            string dbConnectionString = $"User=SYSDBA;Password=masterkey;Database={dbPath};DataSource=localhost;";
            using var connection = new FbConnection(dbConnectionString);
            connection.Open();

            // Wykonaj skrypty w kolejności: domeny, tabele, procedury (dwuetapowo)
            ExecuteScriptsFromDirectory(connection, scriptsDirectory, "domains");
            ExecuteScriptsFromDirectory(connection, scriptsDirectory, "tables");
            
            // Procedury w dwóch etapach aby rozwiązać problem zależności
            ExecuteProcedureHeaders(connection, scriptsDirectory);  // Etap 1: puste procedury
            ExecuteScriptsFromDirectory(connection, scriptsDirectory, "procedures");  // Etap 2: pełny kod

            Console.WriteLine($"Baza danych utworzona: {dbPath}");
        }

        /// <summary>
        /// Generuje skrypty metadanych z istniejącej bazy danych Firebird 5.0.
        /// </summary>
        public static void ExportScripts(string connectionString, string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            using var connection = new FbConnection(connectionString);
            connection.Open();

            Console.WriteLine("Eksportowanie metadanych...");

            // Eksportuj domeny
            ExportDomains(connection, outputDirectory);
            
            // Eksportuj tabele
            ExportTables(connection, outputDirectory);
            
            // Eksportuj procedury
            ExportProcedures(connection, outputDirectory);

            Console.WriteLine($"Metadane wyeksportowane do: {outputDirectory}");
        }

        /// <summary>
        /// Aktualizuje istniejącą bazę danych Firebird 5.0 na podstawie skryptów.
        /// Wykonuje diff - dodaje tylko nowe obiekty, nie duplikuje istniejących.
        /// </summary>
        public static void UpdateDatabase(string connectionString, string scriptsDirectory)
        {
            if (!Directory.Exists(scriptsDirectory))
                throw new DirectoryNotFoundException($"Katalog skryptów nie istnieje: {scriptsDirectory}");

            using var connection = new FbConnection(connectionString);
            connection.Open();

            Console.WriteLine("Aktualizowanie bazy danych...");

            // Pobierz istniejące obiekty z bazy
            var existingDomains = GetExistingDomains(connection);
            var existingTables = GetExistingTables(connection);
            var existingProcedures = GetExistingProcedures(connection);

            // Wykonaj tylko nowe obiekty (diff)
            ExecuteNewDomainsOnly(connection, scriptsDirectory, existingDomains);
            ExecuteNewTablesOnly(connection, scriptsDirectory, existingTables);
            ExecuteNewOrModifiedProcedures(connection, scriptsDirectory, existingProcedures);

            Console.WriteLine("Baza danych zaktualizowana pomyślnie.");
        }

        // Metody pomocnicze
        private static void ExecuteScriptsFromDirectory(FbConnection connection, string scriptsDirectory, string subdirectory)
        {
            string fullPath = Path.Combine(scriptsDirectory, subdirectory);
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine($"Katalog {subdirectory} nie istnieje, pomijam...");
                return;
            }

            var sqlFiles = Directory.GetFiles(fullPath, "*.sql").OrderBy(f => f);
            foreach (string sqlFile in sqlFiles)
            {
                Console.WriteLine($"Wykonuję skrypt: {Path.GetFileName(sqlFile)}");
                try
                {
                    string sql = File.ReadAllText(sqlFile);
                    ExecuteSqlScript(connection, sql);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Błąd w skrypcie {Path.GetFileName(sqlFile)}: {ex.Message}");
                    throw;
                }
            }
        }

        private static void ExecuteSqlScript(FbConnection connection, string sql)
        {
            // Obsługa SET TERM dla procedur
            if (sql.Contains("SET TERM"))
            {
                ExecuteSqlWithSetTerm(connection, sql);
                return;
            }

            // Podziel na pojedyncze polecenia (rozdzielone przez ;)
            var commands = sql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string command in commands)
            {
                string trimmedCommand = command.Trim();
                if (string.IsNullOrEmpty(trimmedCommand)) continue;

                using var cmd = new FbCommand(trimmedCommand, connection);
                cmd.ExecuteNonQuery();
            }
        }

        private static void ExecuteSqlWithSetTerm(FbConnection connection, string sql)
        {
            // Usuń komentarze i puste linie
            var lines = sql.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .Where(line => !string.IsNullOrWhiteSpace(line) && !line.Trim().StartsWith("--"))
                          .ToArray();

            string terminator = "^^";
            var sqlCommands = new List<string>();
            var currentCommand = new StringBuilder();
            bool inSetTerm = false;

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();

                // Sprawdź czy to linia SET TERM
                if (trimmedLine.StartsWith("SET TERM", StringComparison.OrdinalIgnoreCase))
                {
                    if (trimmedLine.Contains("^^"))
                    {
                        // SET TERM ^^ ; - ustawia terminator na ^^
                        terminator = "^^";
                        inSetTerm = true;
                    }
                    else if (trimmedLine.Contains(";"))
                    {
                        // SET TERM ; ^^ - przywraca terminator na ;
                        terminator = ";";
                        inSetTerm = false;
                        
                        // Jeśli mamy zgromadzoną komendę, dodaj ją
                        if (currentCommand.Length > 0)
                        {
                            sqlCommands.Add(currentCommand.ToString().Trim());
                            currentCommand.Clear();
                        }
                    }
                    continue;
                }

                // Dodaj linię do bieżącej komendy
                currentCommand.AppendLine(line);

                // Sprawdź czy linia kończy się terminatorem
                if (trimmedLine.EndsWith(terminator))
                {
                    // Usuń terminator z końca
                    string commandText = currentCommand.ToString();
                    if (terminator == "^^")
                    {
                        commandText = commandText.Replace("^^", "").Trim();
                    }
                    else
                    {
                        commandText = commandText.TrimEnd(';').Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(commandText))
                    {
                        sqlCommands.Add(commandText);
                    }
                    currentCommand.Clear();
                }
            }

            // Jeśli została jakaś komenda, dodaj ją
            if (currentCommand.Length > 0)
            {
                string commandText = currentCommand.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(commandText))
                {
                    sqlCommands.Add(commandText);
                }
            }

            // Wykonaj wszystkie komendy
            foreach (string command in sqlCommands)
            {
                if (string.IsNullOrWhiteSpace(command)) continue;

                try
                {
                    using var cmd = new FbCommand(command, connection);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Błąd wykonywania komendy: {ex.Message}");
                    Console.WriteLine($"Komenda: {command}");
                    throw;
                }
            }
        }

        private static void ExportDomains(FbConnection connection, string outputDirectory)
        {
            string sql = @"
                SELECT RDB$FIELD_NAME, RDB$FIELD_TYPE, RDB$FIELD_LENGTH, RDB$FIELD_SCALE, 
                       RDB$FIELD_SUB_TYPE, RDB$DEFAULT_SOURCE, RDB$VALIDATION_SOURCE
                FROM RDB$FIELDS 
                WHERE RDB$FIELD_NAME NOT STARTING WITH 'RDB$'
                ORDER BY RDB$FIELD_NAME";

            var domains = new List<object>();
            var sqlScript = new StringBuilder();
            sqlScript.AppendLine("-- Domeny");

            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string fieldName = reader["RDB$FIELD_NAME"].ToString()?.Trim();
                int fieldType = Convert.ToInt32(reader["RDB$FIELD_TYPE"]);
                int fieldLength = reader["RDB$FIELD_LENGTH"] != DBNull.Value ? Convert.ToInt32(reader["RDB$FIELD_LENGTH"]) : 0;
                
                string fbType = GetFirebirdTypeName(fieldType, fieldLength);
                
                domains.Add(new
                {
                    Name = fieldName,
                    Type = fbType,
                    Length = fieldLength
                });

                sqlScript.AppendLine($"CREATE DOMAIN {fieldName} AS {fbType};");
            }

            // Zapisz w różnych formatach
            File.WriteAllText(Path.Combine(outputDirectory, "domains.sql"), sqlScript.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "domains.json"), JsonSerializer.Serialize(domains, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void ExportTables(FbConnection connection, string outputDirectory)
        {
            string sql = @"
                SELECT DISTINCT r.RDB$RELATION_NAME
                FROM RDB$RELATIONS r
                WHERE r.RDB$VIEW_BLR IS NULL 
                AND (r.RDB$SYSTEM_FLAG IS NULL OR r.RDB$SYSTEM_FLAG = 0)
                ORDER BY r.RDB$RELATION_NAME";

            var tables = new List<object>();
            var sqlScript = new StringBuilder();
            sqlScript.AppendLine("-- Tabele");

            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            var tableNames = new List<string>();

            while (reader.Read())
            {
                tableNames.Add(reader["RDB$RELATION_NAME"].ToString()?.Trim());
            }

            foreach (string tableName in tableNames)
            {
                var columns = GetTableColumns(connection, tableName);
                tables.Add(new { Name = tableName, Columns = columns });

                sqlScript.AppendLine($"CREATE TABLE {tableName} (");
                for (int i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];
                    string comma = i < columns.Count - 1 ? "," : "";
                    sqlScript.AppendLine($"    {col.Name} {col.Type}{comma}");
                }
                sqlScript.AppendLine(");");
                sqlScript.AppendLine();
            }

            File.WriteAllText(Path.Combine(outputDirectory, "tables.sql"), sqlScript.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "tables.json"), JsonSerializer.Serialize(tables, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void ExportProcedures(FbConnection connection, string outputDirectory)
        {
            string sql = @"
                SELECT RDB$PROCEDURE_NAME, RDB$PROCEDURE_SOURCE
                FROM RDB$PROCEDURES
                WHERE RDB$SYSTEM_FLAG IS NULL OR RDB$SYSTEM_FLAG = 0
                ORDER BY RDB$PROCEDURE_NAME";

            var procedures = new List<object>();
            var sqlScript = new StringBuilder();
            sqlScript.AppendLine("-- Procedury");

            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string procName = reader["RDB$PROCEDURE_NAME"].ToString()?.Trim();
                string procSource = reader["RDB$PROCEDURE_SOURCE"].ToString()?.Trim();

                procedures.Add(new
                {
                    Name = procName,
                    Source = procSource
                });

                if (!string.IsNullOrEmpty(procSource))
                {
                    sqlScript.AppendLine($"CREATE OR ALTER PROCEDURE {procName}");
                    sqlScript.AppendLine(procSource);
                    sqlScript.AppendLine();
                }
            }

            File.WriteAllText(Path.Combine(outputDirectory, "procedures.sql"), sqlScript.ToString());
            File.WriteAllText(Path.Combine(outputDirectory, "procedures.json"), JsonSerializer.Serialize(procedures, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static List<dynamic> GetTableColumns(FbConnection connection, string tableName)
        {
            string sql = @"
                SELECT rf.RDB$FIELD_NAME, rf.RDB$FIELD_POSITION, f.RDB$FIELD_TYPE, 
                       f.RDB$FIELD_LENGTH, f.RDB$FIELD_SCALE, rf.RDB$NULL_FLAG
                FROM RDB$RELATION_FIELDS rf
                JOIN RDB$FIELDS f ON rf.RDB$FIELD_SOURCE = f.RDB$FIELD_NAME
                WHERE rf.RDB$RELATION_NAME = @tableName
                ORDER BY rf.RDB$FIELD_POSITION";

            var columns = new List<dynamic>();
            using var cmd = new FbCommand(sql, connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                string fieldName = reader["RDB$FIELD_NAME"].ToString()?.Trim();
                int fieldType = Convert.ToInt32(reader["RDB$FIELD_TYPE"]);
                int fieldLength = reader["RDB$FIELD_LENGTH"] != DBNull.Value ? Convert.ToInt32(reader["RDB$FIELD_LENGTH"]) : 0;
                bool isNullable = reader["RDB$NULL_FLAG"] == DBNull.Value;

                string fbType = GetFirebirdTypeName(fieldType, fieldLength);
                if (!isNullable) fbType += " NOT NULL";

                columns.Add(new { Name = fieldName, Type = fbType });
            }

            return columns;
        }

        private static string GetFirebirdTypeName(int fieldType, int fieldLength)
        {
            return fieldType switch
            {
                7 => "SMALLINT",
                8 => "INTEGER",
                10 => "FLOAT",
                12 => "DATE",
                13 => "TIME",
                14 => $"CHAR({fieldLength})",
                16 => "BIGINT",
                27 => "DOUBLE PRECISION",
                35 => "TIMESTAMP",
                37 => $"VARCHAR({fieldLength})",
                261 => "BLOB",
                _ => "VARCHAR(255)"
            };
        }

        private static void CreateDatabaseWithIsql(string dbPath)
        {
            // Utwórz bazę używając procesu ISQL
            string isqlPath = FindIsqlPath();
            if (string.IsNullOrEmpty(isqlPath))
            {
                throw new FileNotFoundException("Nie można znaleźć ISQL.exe. Upewnij się, że Firebird jest zainstalowany.");
            }

            string createScript = $"CREATE DATABASE '{dbPath}' USER 'SYSDBA' PASSWORD 'masterkey';";
            string tempScriptFile = Path.GetTempFileName();
            
            try
            {
                File.WriteAllText(tempScriptFile, createScript);
                
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = isqlPath,
                    Arguments = $"-i \"{tempScriptFile}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processInfo);
                process?.WaitForExit();
                
                if (process?.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    throw new Exception($"ISQL zwrócił błąd: {error}");
                }
            }
            finally
            {
                if (File.Exists(tempScriptFile))
                    File.Delete(tempScriptFile);
            }
        }

        private static string FindIsqlPath()
        {
            // Sprawdź typowe lokalizacje ISQL
            string[] possiblePaths = {
                @"C:\Program Files\Firebird\Firebird_5_0\isql.exe",
                @"C:\Program Files (x86)\Firebird\Firebird_5_0\isql.exe",
                @"C:\Program Files\Firebird\Firebird_4_0\isql.exe",
                @"C:\Program Files (x86)\Firebird\Firebird_4_0\isql.exe"
            };

            foreach (string path in possiblePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            // Sprawdź PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(';'))
            {
                string isqlPath = Path.Combine(dir.Trim(), "isql.exe");
                if (File.Exists(isqlPath))
                    return isqlPath;
            }

            return "";
        }

        // Metody dla diff w UpdateDatabase
        private static HashSet<string> GetExistingDomains(FbConnection connection)
        {
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sql = "SELECT RDB$FIELD_NAME FROM RDB$FIELDS WHERE RDB$FIELD_NAME NOT STARTING WITH 'RDB$'";
            
            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string domainName = reader["RDB$FIELD_NAME"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(domainName))
                    domains.Add(domainName);
            }
            return domains;
        }

        private static HashSet<string> GetExistingTables(FbConnection connection)
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sql = @"SELECT r.RDB$RELATION_NAME FROM RDB$RELATIONS r 
                          WHERE r.RDB$VIEW_BLR IS NULL AND (r.RDB$SYSTEM_FLAG IS NULL OR r.RDB$SYSTEM_FLAG = 0)";
            
            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string tableName = reader["RDB$RELATION_NAME"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(tableName))
                    tables.Add(tableName);
            }
            return tables;
        }

        private static HashSet<string> GetExistingProcedures(FbConnection connection)
        {
            var procedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sql = "SELECT RDB$PROCEDURE_NAME FROM RDB$PROCEDURES WHERE RDB$SYSTEM_FLAG IS NULL OR RDB$SYSTEM_FLAG = 0";
            
            using var cmd = new FbCommand(sql, connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string procName = reader["RDB$PROCEDURE_NAME"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(procName))
                    procedures.Add(procName);
            }
            return procedures;
        }

        private static void ExecuteNewDomainsOnly(FbConnection connection, string scriptsDirectory, HashSet<string> existingDomains)
        {
            string fullPath = Path.Combine(scriptsDirectory, "domains");
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine("Katalog domains nie istnieje, pomijam...");
                return;
            }

            var sqlFiles = Directory.GetFiles(fullPath, "*.sql").OrderBy(f => f);
            foreach (string sqlFile in sqlFiles)
            {
                Console.WriteLine($"Sprawdzam domeny w skrypcie: {Path.GetFileName(sqlFile)}");
                string sql = File.ReadAllText(sqlFile);
                ExecuteNewDomainsFromScript(connection, sql, existingDomains);
            }
        }

        private static void ExecuteNewTablesOnly(FbConnection connection, string scriptsDirectory, HashSet<string> existingTables)
        {
            string fullPath = Path.Combine(scriptsDirectory, "tables");
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine("Katalog tables nie istnieje, pomijam...");
                return;
            }

            var sqlFiles = Directory.GetFiles(fullPath, "*.sql").OrderBy(f => f);
            foreach (string sqlFile in sqlFiles)
            {
                Console.WriteLine($"Sprawdzam tabele w skrypcie: {Path.GetFileName(sqlFile)}");
                string sql = File.ReadAllText(sqlFile);
                ExecuteNewTablesFromScript(connection, sql, existingTables);
            }
        }

        private static void ExecuteNewOrModifiedProcedures(FbConnection connection, string scriptsDirectory, HashSet<string> existingProcedures)
        {
            string fullPath = Path.Combine(scriptsDirectory, "procedures");
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine("Katalog procedures nie istnieje, pomijam...");
                return;
            }

            var sqlFiles = Directory.GetFiles(fullPath, "*.sql").OrderBy(f => f);
            foreach (string sqlFile in sqlFiles)
            {
                Console.WriteLine($"Wykonuję procedury z skryptu: {Path.GetFileName(sqlFile)}");
                string sql = File.ReadAllText(sqlFile);
                // Dla procedur używamy CREATE OR ALTER - zawsze działa
                ExecuteSqlScript(connection, sql.Replace("CREATE PROCEDURE", "CREATE OR ALTER PROCEDURE"));
            }
        }

        private static void ExecuteNewDomainsFromScript(FbConnection connection, string sql, HashSet<string> existingDomains)
        {
            var lines = sql.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("CREATE DOMAIN", StringComparison.OrdinalIgnoreCase))
                {
                    // Wyciągnij nazwę domeny
                    var parts = trimmedLine.Split(' ');
                    if (parts.Length >= 3)
                    {
                        string domainName = parts[2];
                        if (!existingDomains.Contains(domainName))
                        {
                            Console.WriteLine($"Dodaję nową domenę: {domainName}");
                            using var cmd = new FbCommand(trimmedLine, connection);
                            cmd.ExecuteNonQuery();
                        }
                        else
                        {
                            Console.WriteLine($"Domena {domainName} już istnieje, pomijam...");
                        }
                    }
                }
            }
        }

        private static void ExecuteNewTablesFromScript(FbConnection connection, string sql, HashSet<string> existingTables)
        {
            // Parsowanie CREATE TABLE jest bardziej skomplikowane - uproszczona wersja
            var commands = sql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string command in commands)
            {
                string trimmedCommand = command.Trim();
                if (trimmedCommand.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    // Wyciągnij nazwę tabeli
                    var lines = trimmedCommand.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        var parts = lines[0].Split(' ');
                        if (parts.Length >= 3)
                        {
                            string tableName = parts[2];
                            if (!existingTables.Contains(tableName))
                            {
                                Console.WriteLine($"Dodaję nową tabelę: {tableName}");
                                using var cmd = new FbCommand(trimmedCommand, connection);
                                cmd.ExecuteNonQuery();
                            }
                            else
                            {
                                Console.WriteLine($"Tabela {tableName} już istnieje, pomijam...");
                            }
                        }
                    }
                }
            }
        }

        // Metoda dla dwuetapowego tworzenia procedur
        private static void ExecuteProcedureHeaders(FbConnection connection, string scriptsDirectory)
        {
            string fullPath = Path.Combine(scriptsDirectory, "procedures");
            if (!Directory.Exists(fullPath))
            {
                Console.WriteLine("Katalog procedures nie istnieje, pomijam tworzenie nagłówków...");
                return;
            }

            Console.WriteLine("Etap 1: Tworzenie pustych procedur (nagłówki)...");
            var sqlFiles = Directory.GetFiles(fullPath, "*.sql").OrderBy(f => f);
            foreach (string sqlFile in sqlFiles)
            {
                string sql = File.ReadAllText(sqlFile);
                CreateEmptyProceduresFromScript(connection, sql);
            }
        }

        private static void CreateEmptyProceduresFromScript(FbConnection connection, string sql)
        {
            // Parsuj procedury i utwórz puste wersje
            var lines = sql.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string currentProcedure = "";
            bool inProcedure = false;

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                
                if (trimmedLine.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) && 
                    trimmedLine.Contains("PROCEDURE"))
                {
                    inProcedure = true;
                    currentProcedure = trimmedLine;
                }
                else if (inProcedure && trimmedLine.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                {
                    // Utwórz pustą procedurę
                    string emptyProc = currentProcedure + "\nAS\nBEGIN\n  -- Placeholder\nEND";
                    try
                    {
                        using var cmd = new FbCommand(emptyProc, connection);
                        cmd.ExecuteNonQuery();
                        Console.WriteLine($"Utworzono pustą procedurę z: {currentProcedure.Split(' ')[3]}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Błąd tworzenia pustej procedury: {ex.Message}");
                        // Kontynuuj - może procedura już istnieje
                    }
                    inProcedure = false;
                    currentProcedure = "";
                }
                else if (inProcedure)
                {
                    currentProcedure += "\n" + line;
                }
            }
        }
    }
}
