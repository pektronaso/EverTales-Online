﻿// Saves Character Data in a SQLite database. We use SQLite for several reasons
//
// - SQLite is file based and works without having to setup a database server
//   - We can 'remove all ...' or 'modify all ...' easily via SQL queries
//   - A lot of people requested a SQL database and weren't comfortable with XML
//   - We can allow all kinds of character names, even chinese ones without
//     breaking the file system.
// - We will need MYSQL or similar when using multiple server instances later
//   and upgrading is trivial
// - XML is easier, but:
//   - we can't easily read 'just the class of a character' etc., but we need it
//     for character selection etc. often
//   - if each account is a folder that contains players, then we can't save
//     additional account info like password, banned, etc. unless we use an
//     additional account.xml file, which over-complicates everything
//   - there will always be forbidden file names like 'COM', which will cause
//     problems when people try to create accounts or characters with that name
//
// About item mall coins:
//   The payment provider's callback should add new orders to the
//   character_orders table. The server will then process them while the player
//   is ingame. Don't try to modify 'coins' in the character table directly.
//
// Tools to open sqlite database files:
//   Windows/OSX program: http://sqlitebrowser.org/
//   Firefox extension: https://addons.mozilla.org/de/firefox/addon/sqlite-manager/
//   Webhost: Adminer/PhpLiteAdmin
//
// About performance:
// - It's recommended to only keep the SQLite connection open while it's used.
//   MMO Servers use it all the time, so we keep it open all the time. This also
//   allows us to use transactions easily, and it will make the transition to
//   MYSQL easier.
// - Transactions are definitely necessary:
//   saving 100 players without transactions takes 3.6s
//   saving 100 players with transactions takes    0.38s
// - Using tr = conn.BeginTransaction() + tr.Commit() and passing it through all
//   the functions is ultra complicated. We use a BEGIN + END queries instead.
//
// Some benchmarks:
//   saving 100 players unoptimized: 4s
//   saving 100 players always open connection + transactions: 3.6s
//   saving 100 players always open connection + transactions + WAL: 3.6s
//   saving 100 players in 1 'using tr = ...' transaction: 380ms
//   saving 100 players in 1 BEGIN/END style transactions: 380ms
//   saving 100 players with XML: 369ms
//   saving 1000 players with mono-sqlite @ 2019-10-03: 843ms
//   saving 1000 players with sqlite-net  @ 2019-10-03:  90ms (!)
//
// Build notes:
// - requires Player settings to be set to '.NET' instead of '.NET Subset',
//   otherwise System.Data.dll causes ArgumentException.
// - requires sqlite3.dll x86 and x64 version for standalone (windows/mac/linux)
//   => found on sqlite.org website
// - requires libsqlite3.so x86 and armeabi-v7a for android
//   => compiled from sqlite.org amalgamation source with android ndk r9b linux
using UnityEngine;
using Mirror;
using System;
using System.IO;
using System.Collections.Generic;
using SQLite; // from https://github.com/praeclarum/sqlite-net
using UnityEngine.AI;
using MySql.Data.MySqlClient;
using System.Data;
using System.Linq;

using SqlParameter = MySql.Data.MySqlClient.MySqlParameter;



public partial class Database : MonoBehaviour
{

    private static string connectionString = null;

    /// <summary>
    /// produces the connection string based on environment variables
    /// </summary>
    /// <value>The connection string</value>
    private static string ConnectionString
    {
        get
        {

            if (connectionString == null)
            {
                var connectionStringBuilder = new MySqlConnectionStringBuilder
                {
                    Server = "localhost",
                    Database = "evertales",
                    UserID = "root",
                    Password = "",
                    Port = 3306,
                    CharacterSet = "utf8",
                    OldGuids = true
                };
                connectionString = connectionStringBuilder.ConnectionString;
            }

            return connectionString;
        }
    }

    private static void Transaction(Action<MySqlCommand> action)
    {
        using (var connection = new MySqlConnection(ConnectionString))
        {

            connection.Open();
            MySqlTransaction transaction = null;

            try
            {

                transaction = connection.BeginTransaction();

                MySqlCommand command = new MySqlCommand();
                command.Connection = connection;
                command.Transaction = transaction;

                action(command);

                transaction.Commit();

            }
            catch (Exception ex)
            {
                if (transaction != null)
                    transaction.Rollback();
                throw ex;
            }
        }
    }

    private static String GetEnv(String name)
    {
        return Environment.GetEnvironmentVariable(name);

    }

    private static uint GetUIntEnv(String name, uint defaultValue = 0)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (value == null)
            return defaultValue;

        uint result;

        if (uint.TryParse(value, out result))
            return result;

        return defaultValue;
    }

    private static void InitializeSchema()
    {
        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS guild_info(
            name VARCHAR(16) NOT NULL,
            notice TEXT NOT NULL,
            PRIMARY KEY(name)
        ) CHARACTER SET=utf8mb4");


        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS accounts (
            name VARCHAR(16) NOT NULL,
            password CHAR(40) NOT NULL,
            banned BOOLEAN NOT NULL DEFAULT 0,
            PRIMARY KEY(name)
        ) CHARACTER SET=utf8mb4");

        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS characters(
            name VARCHAR(16) NOT NULL,
            account VARCHAR(16) NOT NULL,
            class VARCHAR(16) NOT NULL,
            x FLOAT NOT NULL,
        	y FLOAT NOT NULL,
            z FLOAT NOT NULL,
        	level INT NOT NULL DEFAULT 1,
            health INT NOT NULL,
        	mana INT NOT NULL,
            strength INT NOT NULL DEFAULT 0,
        	intelligence INT NOT NULL DEFAULT 0,
            experience BIGINT NOT NULL DEFAULT 0,
        	skillExperience BIGINT NOT NULL DEFAULT 0,
            gold BIGINT NOT NULL DEFAULT 0,
        	coins BIGINT NOT NULL DEFAULT 0,
            online TIMESTAMP,
            deleted BOOLEAN NOT NULL,
            guild VARCHAR(16),
            `rank` INT,
        	PRIMARY KEY (name),
            INDEX(account),
            INDEX(guild),
        	FOREIGN KEY(account)
                REFERENCES accounts(name)
                ON DELETE CASCADE ON UPDATE CASCADE,
            FOREIGN KEY(guild)
                REFERENCES guild_info(name)
                ON DELETE SET NULL ON UPDATE CASCADE
        ) CHARACTER SET=utf8mb4");


        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_inventory(
            `character` VARCHAR(16) NOT NULL,
            slot INT NOT NULL,
        	name VARCHAR(50) NOT NULL,
            amount INT NOT NULL,
        	summonedHealth INT NOT NULL,
            summonedLevel INT NOT NULL,
            summonedExperience BIGINT NOT NULL,
            primary key(`character`, slot),
        	FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE
        ) CHARACTER SET=utf8mb4");

        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_equipment(
            `character` VARCHAR(16) NOT NULL,
            slot INT NOT NULL,
        	name VARCHAR(50) NOT NULL,
            amount INT NOT NULL,
            primary key(`character`, slot),
        	FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE
         ) CHARACTER SET=utf8mb4");

        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_skills(
            `character` VARCHAR(16) NOT NULL,
            name VARCHAR(50) NOT NULL,
            level INT NOT NULL,
        	castTimeEnd FLOAT NOT NULL,
            cooldownEnd FLOAT NOT NULL,
            PRIMARY KEY (`character`, name),
            FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE
        ) CHARACTER SET=utf8mb4");


        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_buffs (
            `character` VARCHAR(16) NOT NULL,
            name VARCHAR(50) NOT NULL,
            level INT NOT NULL,
            buffTimeEnd FLOAT NOT NULL,
            PRIMARY KEY (`character`, name),
            FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE 
        ) CHARACTER SET=utf8mb4");


        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_quests(
            `character` VARCHAR(16) NOT NULL,
            name VARCHAR(50) NOT NULL,
            field0 INT NOT NULL,
        	completed BOOLEAN NOT NULL,
            PRIMARY KEY(`character`, name),
        	FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE
        ) CHARACTER SET=utf8mb4");


        ExecuteNonQueryMySql(@"
        CREATE TABLE IF NOT EXISTS character_orders(
            orderid BIGINT NOT NULL AUTO_INCREMENT,
            `character` VARCHAR(16) NOT NULL,
            coins BIGINT NOT NULL,
            processed BIGINT NOT NULL,
            PRIMARY KEY(orderid),
            INDEX(`character`),
        	FOREIGN KEY(`character`)
                REFERENCES characters(name)
                ON DELETE CASCADE ON UPDATE CASCADE
        ) CHARACTER SET=utf8mb4");
    }

    static Database()
    {
        Debug.Log("Initializing MySQL database");

        InitializeSchema();

        Utils.InvokeMany(typeof(Database), null, "Initialize_");
    }

    #region Helper Functions

    // run a query that doesn't return anything
    private static void ExecuteNonQueryMySql(string sql, params SqlParameter[] args)
    {
        try
        {
            MySqlHelper.ExecuteNonQuery(ConnectionString, sql, args);
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }

    }


    private static void ExecuteNonQueryMySql(MySqlCommand command, string sql, params SqlParameter[] args)
    {
        try
        {
            command.CommandText = sql;
            command.Parameters.Clear();

            foreach (var arg in args)
            {
                command.Parameters.Add(arg);
            }

            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }

    }

    // run a query that returns a single value
    private static object ExecuteScalarMySql(string sql, params SqlParameter[] args)
    {
        try
        {
            return MySqlHelper.ExecuteScalar(ConnectionString, sql, args);
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }
    }

    private static DataRow ExecuteDataRowMySql(string sql, params SqlParameter[] args)
    {
        try
        {
            return MySqlHelper.ExecuteDataRow(ConnectionString, sql, args);
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }
    }

    private static DataSet ExecuteDataSetMySql(string sql, params SqlParameter[] args)
    {
        try
        {
            return MySqlHelper.ExecuteDataset(ConnectionString, sql, args);
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }
    }

    // run a query that returns several values
    private static List<List<object>> ExecuteReaderMySql(string sql, params SqlParameter[] args)
    {
        try
        {
            var result = new List<List<object>>();

            using (var reader = MySqlHelper.ExecuteReader(ConnectionString, sql, args))
            {

                while (reader.Read())
                {
                    var buf = new object[reader.FieldCount];
                    reader.GetValues(buf);
                    result.Add(buf.ToList());
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }

    }

    // run a query that returns several values
    private static MySqlDataReader GetReader(string sql, params SqlParameter[] args)
    {
        try
        {
            return MySqlHelper.ExecuteReader(ConnectionString, sql, args);
        }
        catch (Exception ex)
        {
            Debug.LogErrorFormat("Failed to execute query {0}", sql);
            throw ex;
        }
    }

    #endregion


    // account data ////////////////////////////////////////////////////////////
    public static bool IsValidAccount(string account, string password)
    {
        // this function can be used to verify account credentials in a database
        // or a content management system.
        //
        // for example, we could setup a content management system with a forum,
        // news, shop etc. and then use a simple HTTP-GET to check the account
        // info, for example:
        //
        //   var request = new WWW("example.com/verify.php?id="+id+"&amp;pw="+pw);
        //   while (!request.isDone)
        //       print("loading...");
        //   return request.error == null && request.text == "ok";
        //
        // where verify.php is a script like this one:
        //   <?php
        //   // id and pw set with HTTP-GET?
        //   if (isset($_GET['id']) && isset($_GET['pw'])) {
        //       // validate id and pw by using the CMS, for example in Drupal:
        //       if (user_authenticate($_GET['id'], $_GET['pw']))
        //           echo "ok";
        //       else
        //           echo "invalid id or pw";
        //   }
        //   ?>
        //
        // or we could check in a MYSQL database:
        //   var dbConn = new MySql.Data.MySqlClient.MySqlConnection("Persist Security Info=False;server=localhost;database=notas;uid=root;password=" + dbpwd);
        //   var cmd = dbConn.CreateCommand();
        //   cmd.CommandText = "SELECT id FROM accounts WHERE id='" + account + "' AND pw='" + password + "'";
        //   dbConn.Open();
        //   var reader = cmd.ExecuteReader();
        //   if (reader.Read())
        //       return reader.ToString() == account;
        //   return false;
        //
        // as usual, we will use the simplest solution possible:
        // create account if not exists, compare password otherwise.
        // no CMS communication necessary and good enough for an Indie MMORPG.

        // not empty?
        if (!String.IsNullOrWhiteSpace(account) && !String.IsNullOrWhiteSpace(password))
        {

            var row = ExecuteDataRowMySql("SELECT password, banned FROM accounts WHERE name=@name", new SqlParameter("@name", account));
            if (row != null)
            {
                return password == (string)row["password"] && !(bool)row["banned"];
            }
            else
            {
                // account doesn't exist. create it.
                ExecuteNonQueryMySql("INSERT INTO accounts VALUES (@name, @password, 0)", new SqlParameter("@name", account), new SqlParameter("@password", password));
                return true;
            }
        }
        return false;
    }

    // character data //////////////////////////////////////////////////////////
    public static bool CharacterExists(string characterName)
    {
        // checks deleted ones too so we don't end up with duplicates if we un-
        // delete one
        return ((long)ExecuteScalarMySql("SELECT Count(*) FROM characters WHERE name=@name", new SqlParameter("@name", characterName))) == 1;
    }

    public static void CharacterDelete(string characterName)
    {
        // soft delete the character so it can always be restored later
        ExecuteNonQueryMySql("UPDATE characters SET deleted=1 WHERE name=@character", new SqlParameter("@character", characterName));
    }

    // returns a dict of<character name, character class=prefab name>
    // we really need the prefab name too, so that client character selection
    // can read all kinds of properties like icons, stats, 3D models and not
    // just the character name
    public static List<string> CharactersForAccount(string account)
    {
        var result = new List<String>();

        var table = ExecuteReaderMySql("SELECT name FROM characters WHERE account=@account AND deleted=0", new SqlParameter("@account", account));
        foreach (var row in table)
            result.Add((string)row[0]);
        return result;
    }

    private static void LoadInventory(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.inventorySize; ++i)
            player.inventory.Add(new ItemSlot());

        // override with the inventory stored in database
        using (var reader = GetReader(@"SELECT * FROM character_inventory WHERE `character`=@character;",
                                           new SqlParameter("@character", player.name)))
        {

            while (reader.Read())
            {
                string itemName = (string)reader["name"];
                int slot = (int)reader["slot"];

                ScriptableItem itemData;
                if (slot < player.inventorySize && ScriptableItem.dict.TryGetValue(itemName.GetStableHashCode(), out itemData))
                {
                    Item item = new Item(itemData);
                    int amount = (int)reader["amount"];
                    item.summonedHealth = (int)reader["summonedHealth"];
                    item.summonedLevel = (int)reader["summonedLevel"];
                    item.summonedExperience = (long)reader["summonedExperience"];
                    player.inventory[slot] = new ItemSlot(item, amount); ;
                }
            }
        }
    }

    private static void LoadEquipment(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.equipmentInfo.Length; ++i)
            player.equipment.Add(new ItemSlot());

        using (var reader = GetReader(@"SELECT * FROM character_equipment WHERE `character`=@character;",
                                           new SqlParameter("@character", player.name)))
        {

            while (reader.Read())
            {
                string itemName = (string)reader["name"];
                int slot = (int)reader["slot"];

                ScriptableItem itemData;
                if (slot < player.equipmentInfo.Length && ScriptableItem.dict.TryGetValue(itemName.GetStableHashCode(), out itemData))
                {
                    Item item = new Item(itemData);
                    int amount = (int)reader["amount"];
                    player.equipment[slot] = new ItemSlot(item, amount);
                }
            }
        }
    }

    private static void LoadSkills(Player player)
    {
        // load skills based on skill templates (the others don't matter)
        // -> this way any template changes in a prefab will be applied
        //    to all existing players every time (unlike item templates
        //    which are only for newly created characters)

        // fill all slots first
        foreach (ScriptableSkill skillData in player.skillTemplates)
            player.skills.Add(new Skill(skillData));

        using (var reader = GetReader(
            "SELECT name, level, castTimeEnd, cooldownEnd FROM character_skills WHERE `character`=@character ",
            new SqlParameter("@character", player.name)))
        {

            while (reader.Read())
            {

                var skillName = (string)reader["name"];

                int index = player.skills.FindIndex(skill => skill.name == skillName);
                if (index != -1)
                {
                    Skill skill = player.skills[index];
                    // make sure that 1 <= level <= maxlevel (in case we removed a skill
                    // level etc)
                    skill.level = Mathf.Clamp((int)reader["level"], 1, skill.maxLevel);
                    // make sure that 1 <= level <= maxlevel (in case we removed a skill
                    // level etc)
                    // castTimeEnd and cooldownEnd are based on Time.time, which
                    // will be different when restarting a server, hence why we
                    // saved them as just the remaining times. so let's convert them
                    // back again.
                    skill.castTimeEnd = (float)reader["castTimeEnd"] + Time.time;
                    skill.cooldownEnd = (float)reader["cooldownEnd"] + Time.time;

                    player.skills[index] = skill;
                }
            }
        }
    }

    private static void LoadBuffs(Player player)
    {

        using (var reader = GetReader(
            "SELECT name, level, buffTimeEnd FROM character_buffs WHERE `character` = @character ",
            new SqlParameter("@character", player.name)))
        {
            while (reader.Read())
            {
                string buffName = (string)reader["name"];
                ScriptableSkill skillData;
                if (ScriptableSkill.dict.TryGetValue(buffName.GetStableHashCode(), out skillData))
                {
                    // make sure that 1 <= level <= maxlevel (in case we removed a skill
                    // level etc)
                    int level = Mathf.Clamp((int)reader["level"], 1, skillData.maxLevel);
                    Buff buff = new Buff((BuffSkill)skillData, level);
                    // buffTimeEnd is based on Time.time, which will be
                    // different when restarting a server, hence why we saved
                    // them as just the remaining times. so let's convert them
                    // back again.
                    buff.buffTimeEnd = (float)reader["buffTimeEnd"] + Time.time;
                    player.buffs.Add(buff);
                }
            }

        }
    }

    private static void LoadQuests(Player player)
    {
        // load quests

        using (var reader = GetReader("SELECT name, field0, completed FROM character_quests WHERE `character`=@character",
                                           new SqlParameter("@character", player.name)))
        {

            while (reader.Read())
            {
                string questName = (string)reader["name"];
                ScriptableQuest questData;
                if (ScriptableQuest.dict.TryGetValue(questName.GetStableHashCode(), out questData))
                {
                    Quest quest = new Quest(questData);
                    quest.field0 = (int)reader["field0"];
                    quest.completed = (bool)reader["completed"];
                    player.quests.Add(quest);
                }
            }
        }
    }

    private static void LoadGuild(Player player)
    {
        // in a guild?
        if (player.guild.name != "")
        {
            // load guild info
            var row = ExecuteDataRowMySql("SELECT notice FROM guild_info WHERE name=@guild", new SqlParameter("@guild", player.guild.name));
            if (row != null)
            {
                player.guild.notice = (string)row["notice"];
            }

            // load members list
            var members = new List<GuildMember>();

            using (var reader = GetReader(
                "SELECT name, level, `rank` FROM characters WHERE guild=@guild AND deleted=0",
                new SqlParameter("@guild", player.guild.name)))
            {

                while (reader.Read())
                {
                    var member = new GuildMember();
                    member.name = (string)reader["name"];
                    member.rank = (GuildRank)((int)reader["rank"]);
                    member.online = Player.onlinePlayers.ContainsKey(member.name);
                    member.level = (int)reader["level"];

                    members.Add(member);
                };
            }
            player.guild.members = members.ToArray(); // guild.AddMember each time is too slow because array resizing
        }
    }

    public static GameObject CharacterLoad(string characterName, List<Player> prefabs)
    {
        var row = ExecuteDataRowMySql("SELECT * FROM characters WHERE name=@name AND deleted=0", new SqlParameter("@name", characterName));
        if (row != null)
        {
            // instantiate based on the class name
            string className = (string)row["class"];
            var prefab = prefabs.Find(p => p.name == className);
            if (prefab != null)
            {
                var go = GameObject.Instantiate(prefab.gameObject);
                var player = go.GetComponent<Player>();

                player.name = (string)row["name"];
                player.account = (string)row["account"];
                player.className = (string)row["class"];
                float x = (float)row["x"];
                float y = (float)row["y"];
                float z = (float)row["z"];
                Vector3 position = new Vector3(x, y, z);
                player.level = (int)row["level"];
                int health = (int)row["health"];
                int mana = (int)row["mana"];
                player.strength = (int)row["strength"];
                player.intelligence = (int)row["intelligence"];
                player.experience = (long)row["experience"];
                player.skillExperience = (long)row["skillExperience"];
                player.gold = (long)row["gold"];
                player.coins = (long)row["coins"];

                if (row.IsNull("guild"))
                    player.guild.name = "";
                else
                    player.guild.name = (string)row["guild"];

                // try to warp to loaded position.
                // => agent.warp is recommended over transform.position and
                //    avoids all kinds of weird bugs
                // => warping might fail if we changed the world since last save
                //    so we reset to start position if not on navmesh
                player.agent.Warp(position);
                if (!player.agent.isOnNavMesh)
                {
                    Transform start = NetworkManagerMMO.GetNearestStartPosition(position);
                    player.agent.Warp(start.position);
                    Debug.Log(player.name + " invalid position was reset");
                }

                LoadInventory(player);
                LoadEquipment(player);
                LoadSkills(player);
                LoadBuffs(player);
                LoadQuests(player);
                LoadGuild(player);

                // assign health / mana after max values were fully loaded
                // (they depend on equipment, buffs, etc.)
                player.health = health;
                player.mana = mana;

                // addon system hooks
                //Utils.InvokeMany(typeof(Database), null, "CharacterLoad_", player);

                return go;
            }
            else Debug.LogError("no prefab found for class: " + className);
        }
        return null;
    }

    static void SaveInventory(Player player, MySqlCommand command)
    {
        // inventory: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQueryMySql(command, "DELETE FROM character_inventory WHERE `character`=@character", new SqlParameter("@character", player.name));
        for (int i = 0; i < player.inventory.Count; ++i)
        {
            ItemSlot slot = player.inventory[i];
            if (slot.amount > 0) // only relevant items to save queries/storage/time
                ExecuteNonQueryMySql(command, "INSERT INTO character_inventory VALUES (@character, @slot, @name, @amount, @summonedHealth, @summonedLevel, @summonedExperience)",
                        new SqlParameter("@character", player.name),
                        new SqlParameter("@slot", i),
                        new SqlParameter("@name", slot.item.name),
                        new SqlParameter("@amount", slot.amount),
                        new SqlParameter("@summonedHealth", slot.item.summonedHealth),
                        new SqlParameter("@summonedLevel", slot.item.summonedLevel),
                        new SqlParameter("@summonedExperience", slot.item.summonedExperience));
        }
    }

    static void SaveEquipment(Player player, MySqlCommand command)
    {
        // equipment: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        ExecuteNonQueryMySql(command, "DELETE FROM character_equipment WHERE `character`=@character", new SqlParameter("@character", player.name));
        for (int i = 0; i < player.equipment.Count; ++i)
        {
            ItemSlot slot = player.equipment[i];
            if (slot.amount > 0) // only relevant equip to save queries/storage/time
                ExecuteNonQueryMySql(command, "INSERT INTO character_equipment VALUES (@character, @slot, @name, @amount)",
                            new SqlParameter("@character", player.name),
                            new SqlParameter("@slot", i),
                            new SqlParameter("@name", slot.item.name),
                            new SqlParameter("@amount", slot.amount));
        }
    }

    static void SaveSkills(Player player, MySqlCommand command)
    {
        // skills: remove old entries first, then add all new ones
        ExecuteNonQueryMySql(command, "DELETE FROM character_skills WHERE `character`=@character", new SqlParameter("@character", player.name));
        foreach (var skill in player.skills)
        {
            // only save relevant skills to save a lot of queries and storage
            // (considering thousands of players)
            // => interesting only if learned or if buff/status (murderer etc.)
            if (skill.level > 0) // only relevant skills to save queries/storage/time
            {
                // castTimeEnd and cooldownEnd are based on Time.time, which
                // will be different when restarting the server, so let's
                // convert them to the remaining time for easier save & load
                // note: this does NOT work when trying to save character data shortly
                //       before closing the editor or game because Time.time is 0 then.
                ExecuteNonQueryMySql(command, @"
                    INSERT INTO character_skills 
                    SET
                        `character` = @character,
                        name = @name,
                        level = @level,
                        castTimeEnd = @castTimeEnd,
                        cooldownEnd = @cooldownEnd",
                                    new SqlParameter("@character", player.name),
                                    new SqlParameter("@name", skill.name),
                                     new SqlParameter("@level", skill.level),
                                    new SqlParameter("@castTimeEnd", skill.CastTimeRemaining()),
                                    new SqlParameter("@cooldownEnd", skill.CooldownRemaining()));
            }
        }
    }

    static void SaveBuffs(Player player, MySqlCommand command)
    {
        ExecuteNonQueryMySql(command, "DELETE FROM character_buffs WHERE `character`=@character", new SqlParameter("@character", player.name));
        foreach (var buff in player.buffs)
        {
            // buffTimeEnd is based on Time.time, which will be different when
            // restarting the server, so let's convert them to the remaining
            // time for easier save & load
            // note: this does NOT work when trying to save character data shortly
            //       before closing the editor or game because Time.time is 0 then.
            ExecuteNonQueryMySql(command, "INSERT INTO character_buffs VALUES (@character, @name, @level, @buffTimeEnd)",
                            new SqlParameter("@character", player.name),
                            new SqlParameter("@name", buff.name),
                                 new SqlParameter("@level", buff.level),
                            new SqlParameter("@buffTimeEnd", (float)buff.BuffTimeRemaining()));
        }
    }

    static void SaveQuests(Player player, MySqlCommand command)
    {
        // quests: remove old entries first, then add all new ones
        ExecuteNonQueryMySql(command, "DELETE FROM character_quests WHERE `character`=@character", new SqlParameter("@character", player.name));
        foreach (var quest in player.quests)
        {
            ExecuteNonQueryMySql(command, "INSERT INTO character_quests VALUES (@character, @name, @field0, @completed)",
                            new SqlParameter("@character", player.name),
                            new SqlParameter("@name", quest.name),
                            new SqlParameter("@field0", quest.field0),
                            new SqlParameter("@completed", quest.completed));
        }
    }

    // adds or overwrites character data in the database
    static void CharacterSave(Player player, bool online, MySqlCommand command)
    {
        // online status:
        //   '' if offline (if just logging out etc.)
        //   current time otherwise
        // -> this way it's fault tolerant because external applications can
        //    check if online != '' and if time difference < saveinterval
        // -> online time is useful for network zones (server<->server online
        //    checks), external websites which render dynamic maps, etc.
        // -> it uses the ISO 8601 standard format
        DateTime? onlineTimestamp = null;

        if (!online)
            onlineTimestamp = DateTime.Now;

        var query = @"
            INSERT INTO characters 
            SET
                name=@name,
                account=@account,
                class = @class,
                x = @x,
                y = @y,
                z = @z,
                level = @level,
                health = @health,
                mana = @mana,
                strength = @strength,
                intelligence = @intelligence,
                experience = @experience,
                skillExperience = @skillExperience,
                gold = @gold,
                coins = @coins,
                online = @online,
                deleted = 0,
                guild = @guild
            ON DUPLICATE KEY UPDATE 
                account=@account,
                class = @class,
                x = @x,
                y = @y,
                z = @z,
                level = @level,
                health = @health,
                mana = @mana,
                strength = @strength,
                intelligence = @intelligence,
                experience = @experience,
                skillExperience = @skillExperience,
                gold = @gold,
                coins = @coins,
                online = @online,
                deleted = 0,
                guild = @guild
            ";

        ExecuteNonQueryMySql(command, query,
                    new SqlParameter("@name", player.name),
                    new SqlParameter("@account", player.account),
                    new SqlParameter("@class", player.className),
                    new SqlParameter("@x", player.transform.position.x),
                    new SqlParameter("@y", player.transform.position.y),
                    new SqlParameter("@z", player.transform.position.z),
                    new SqlParameter("@level", player.level),
                    new SqlParameter("@health", player.health),
                    new SqlParameter("@mana", player.mana),
                    new SqlParameter("@strength", player.strength),
                    new SqlParameter("@intelligence", player.intelligence),
                    new SqlParameter("@experience", player.experience),
                    new SqlParameter("@skillExperience", player.skillExperience),
                    new SqlParameter("@gold", player.gold),
                    new SqlParameter("@coins", player.coins),
                    new SqlParameter("@online", onlineTimestamp),
                    new SqlParameter("@guild", player.guild.name == "" ? null : player.guild.name)
                       );

        SaveInventory(player, command);
        SaveEquipment(player, command);
        SaveSkills(player, command);
        SaveBuffs(player, command);
        SaveQuests(player, command);

        // addon system hooks
        //Utils.InvokeMany(typeof(Database), null, "CharacterSave_", player);
    }

    // adds or overwrites character data in the database
    public static void CharacterSave(Player player, bool online, bool useTransaction = true)
    {
        // only use a transaction if not called within SaveMany transaction
        Transaction(command =>
        {
            CharacterSave(player, online, command);
        });
    }



    // save multiple characters at once (useful for ultra fast transactions)
    public static void CharacterSaveMany(List<Player> players, bool online = true)
    {
        Transaction(command =>
        {
            foreach (var player in players)
                CharacterSave(player, online, command);
        });
    }

    // guilds //////////////////////////////////////////////////////////////////
    public static void SaveGuild(string guild, string notice, List<GuildMember> members)
    {
        Transaction(command =>
        {
            var query = @"
            INSERT INTO guild_info
            SET
                name = @guild,
                notice = @notice
            ON DUPLICATE KEY UPDATE
                notice = @notice";

            // guild info
            ExecuteNonQueryMySql(command, query,
                                new SqlParameter("@guild", guild),
                                new SqlParameter("@notice", notice));

            ExecuteNonQueryMySql(command, "UPDATE characters set guild = NULL where guild=@guild", new SqlParameter("@guild", guild));


            foreach (var member in members)
            {

                Debug.Log("Saving guild " + guild + " member " + member.name);
                ExecuteNonQueryMySql(command, "UPDATE characters set guild = @guild, `rank`=@rank where name=@character",
                                new SqlParameter("@guild", guild),
                                new SqlParameter("@character", member.name),
                                new SqlParameter("@rank", member.rank));
            }
        });
    }

    public static bool GuildExists(string guild)
    {
        return ((long)ExecuteScalarMySql("SELECT Count(*) FROM guild_info WHERE name=@name", new SqlParameter("@name", guild))) == 1;
    }

    public static void RemoveGuild(string guild)
    {
        ExecuteNonQueryMySql("DELETE FROM guild_info WHERE name=@name", new SqlParameter("@name", guild));
    }

    // item mall ///////////////////////////////////////////////////////////////
    public static List<long> GrabCharacterOrders(string characterName)
    {
        // grab new orders from the database and delete them immediately
        //
        // note: this requires an orderid if we want someone else to write to
        // the database too. otherwise deleting would delete all the new ones or
        // updating would update all the new ones. especially in sqlite.
        //
        // note: we could just delete processed orders, but keeping them in the
        // database is easier for debugging / support.
        var result = new List<long>();
        var table = ExecuteReaderMySql("SELECT orderid, coins FROM character_orders WHERE `character`=@character AND processed=0", new SqlParameter("@character", characterName));
        foreach (var row in table)
        {
            result.Add((long)row[1]);
            ExecuteNonQueryMySql("UPDATE character_orders SET processed=1 WHERE orderid=@orderid", new SqlParameter("@orderid", (long)row[0]));
        }
        return result;
    }
}