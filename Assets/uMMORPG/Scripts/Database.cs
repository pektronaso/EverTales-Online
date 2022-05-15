// Saves Character Data in a SQLite database. We use SQLite for several reasons
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

public partial class Database : MonoBehaviour
{
    // singleton for easier access
    public static Database singleton;

    // file name
    public string databaseFile = "Database.sqlite";

    // connection
    SQLiteConnection connection;

    // database layout via .NET classes:
    // https://github.com/praeclarum/sqlite-net/wiki/GettingStarted
    class accounts
    {
        [PrimaryKey] // important for performance: O(log n) instead of O(n)
        public string name { get; set; }
        public string password { get; set; }
        // created & lastlogin for statistics like CCU/MAU/registrations/...
        public DateTime created { get; set; }
        public DateTime lastlogin { get; set; }
        public bool banned { get; set; }
    }
    class characters
    {
        [PrimaryKey] // important for performance: O(log n) instead of O(n)
        [Collation("NOCASE")] // [COLLATE NOCASE for case insensitive compare. this way we can't both create 'Archer' and 'archer' as characters]
        public string name { get; set; }
        [Indexed] // add index on account to avoid full scans when loading characters
        public string account { get; set; }
        public string classname { get; set; } // 'class' isn't available in C#
        public float x { get; set; }
        public float y { get; set; }
        public int level { get; set; }
        public int health { get; set; }
        public int mana { get; set; }
        public int strength { get; set; }
        public int intelligence { get; set; }
        public long experience { get; set; } // TODO does long work?
        public long skillExperience { get; set; } // TODO does long work?
        public long gold { get; set; } // TODO does long work?
        public long coins { get; set; } // TODO does long work?
        // online status can be checked from external programs with either just
        // just 'online', or 'online && (DateTime.UtcNow - lastsaved) <= 1min)
        // which is robust to server crashes too.
        public bool online { get; set; }
        public DateTime lastsaved { get; set; }
        public bool deleted { get; set; }
    }
    class character_inventory
    {
        public string character { get; set; }
        public int slot { get; set; }
        public string name { get; set; }
        public int amount { get; set; }
        public int summonedHealth { get; set; }
        public int summonedLevel { get; set; }
        public long summonedExperience { get; set; } // TODO does long work?
        // PRIMARY KEY (character, slot) is created manually.
    }
    class character_equipment : character_inventory // same layout
    {
        // PRIMARY KEY (character, slot) is created manually.
    }
    class character_skills
    {
        public string character { get; set; }
        public string name { get; set; }
        public int level { get; set; }
        public float castTimeEnd { get; set; }
        public float cooldownEnd { get; set; }
        // PRIMARY KEY (character, name) is created manually.
    }
    class character_buffs
    {
        public string character { get; set; }
        public string name { get; set; }
        public int level { get; set; }
        public float buffTimeEnd { get; set; }
        // PRIMARY KEY (character, name) is created manually.
    }
    class character_quests
    {
        public string character { get; set; }
        public string name { get; set; }
        public int progress { get; set; }
        public bool completed { get; set; }
        // PRIMARY KEY (character, name) is created manually.
    }
    class character_orders
    {
        // INTEGER PRIMARY KEY is auto incremented by sqlite if the insert call
        // passes NULL for it.
        [PrimaryKey] // important for performance: O(log n) instead of O(n)
        public int orderid { get; set; }
        public string character { get; set; }
        public long coins { get; set; }
        public bool processed { get; set; }
    }
    class character_guild
    {
        // guild members are saved in a separate table because instead of in a
        // characters.guild field because:
        // * guilds need to be resaved independently, not just in CharacterSave
        // * kicked members' guilds are cleared automatically because we drop
        //   and then insert all members each time. otherwise we'd have to
        //   update the kicked member's guild field manually each time
        // * it's easier to remove / modify the guild feature if it's not hard-
        //   coded into the characters table
        [PrimaryKey] // important for performance: O(log n) instead of O(n)
        public string character { get; set; }
        // add index on guild to avoid full scans when loading guild members
        [Indexed]
        public string guild { get; set; }
        public int rank { get; set; }
    }
    class guild_info
    {
        // guild master is not in guild_info in case we need more than one later
        [PrimaryKey] // important for performance: O(log n) instead of O(n)
        public string name { get; set; }
        public string notice { get; set; }
    }

    void Awake()
    {
        // initialize singleton
        if (singleton == null) singleton = this;
    }

    // connect /////////////////////////////////////////////////////////////////
    // only call this from the server, not from the client. otherwise the client
    // would create a database file / webgl would throw errors, etc.
    public void Connect()
    {
        // database path: Application.dataPath is always relative to the project,
        // but we don't want it inside the Assets folder in the Editor (git etc.),
        // instead we put it above that.
        // we also use Path.Combine for platform independent paths
        // and we need persistentDataPath on android
#if UNITY_EDITOR
        string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, databaseFile);
#elif UNITY_ANDROID
        string path = Path.Combine(Application.persistentDataPath, databaseFile);
#elif UNITY_IOS
        string path = Path.Combine(Application.persistentDataPath, databaseFile);
#else
        string path = Path.Combine(Application.dataPath, databaseFile);
#endif

        // open connection
        // note: automatically creates database file if not created yet
        connection = new SQLiteConnection(path);

        // create tables if they don't exist yet or were deleted
        connection.CreateTable<accounts>();
        connection.CreateTable<characters>();
        connection.CreateTable<character_inventory>();
        connection.CreateIndex(nameof(character_inventory), new []{"character", "slot"});
        connection.CreateTable<character_equipment>();
        connection.CreateIndex(nameof(character_equipment), new []{"character", "slot"});
        connection.CreateTable<character_skills>();
        connection.CreateIndex(nameof(character_skills), new []{"character", "name"});
        connection.CreateTable<character_buffs>();
        connection.CreateIndex(nameof(character_buffs), new []{"character", "name"});
        connection.CreateTable<character_quests>();
        connection.CreateIndex(nameof(character_quests), new []{"character", "name"});
        connection.CreateTable<character_orders>();
        connection.CreateTable<character_guild>();
        connection.CreateTable<guild_info>();

        // addon system hooks
        Utils.InvokeMany(typeof(Database), this, "Initialize_"); // TODO remove later. let's keep the old hook for a while to not break every single addon!
        Utils.InvokeMany(typeof(Database), this, "Connect_"); // the new hook!

        //Debug.Log("connected to database");
    }

    // close connection when Unity closes to prevent locking
    void OnApplicationQuit()
    {
        connection?.Close();
    }

    // account data ////////////////////////////////////////////////////////////
    // try to log in with an account.
    // -> not called 'CheckAccount' or 'IsValidAccount' because it both checks
    //    if the account is valid AND sets the lastlogin field
    public bool TryLogin(string account, string password)
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
        if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(password))
        {
            // demo feature: create account if it doesn't exist yet.
            // note: sqlite-net has no InsertOrIgnore so we do it in two steps
            if (connection.FindWithQuery<accounts>("SELECT * FROM accounts WHERE name=?", account) == null)
                connection.Insert(new accounts{ name=account, password=password, created=DateTime.UtcNow, lastlogin=DateTime.Now, banned=false});

            // check account name, password, banned status
            if (connection.FindWithQuery<accounts>("SELECT * FROM accounts WHERE name=? AND password=? and banned=0", account, password) != null)
            {
                // save last login time and return true
                connection.Execute("UPDATE accounts SET lastlogin=? WHERE name=?", DateTime.UtcNow, account);
                return true;
            }
        }
        return false;
    }

    // character data //////////////////////////////////////////////////////////
    public bool CharacterExists(string characterName)
    {
        // checks deleted ones too so we don't end up with duplicates if we un-
        // delete one
        return connection.FindWithQuery<characters>("SELECT * FROM characters WHERE name=?", characterName) != null;
    }

    public void CharacterDelete(string characterName)
    {
        // soft delete the character so it can always be restored later
        connection.Execute("UPDATE characters SET deleted=1 WHERE name=?", characterName);
    }

    // returns the list of character names for that account
    // => all the other values can be read with CharacterLoad!
    public List<string> CharactersForAccount(string account)
    {
        List<string> result = new List<string>();
        foreach (characters character in connection.Query<characters>("SELECT * FROM characters WHERE account=? AND deleted=0", account))
            result.Add(character.name);
        return result;
    }

    void LoadInventory(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.inventorySize; ++i)
            player.inventory.Add(new ItemSlot());

        // then load valid items and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        foreach (character_inventory row in connection.Query<character_inventory>("SELECT * FROM character_inventory WHERE character=?", player.name))
        {
            if (row.slot < player.inventorySize)
            {
                if (ScriptableItem.dict.TryGetValue(row.name.GetStableHashCode(), out ScriptableItem itemData))
                {
                    Item item = new Item(itemData);
                    item.summonedHealth = row.summonedHealth;
                    item.summonedLevel = row.summonedLevel;
                    item.summonedExperience = row.summonedExperience;
                    player.inventory[row.slot] = new ItemSlot(item, row.amount);
                }
                else Debug.LogWarning("LoadInventory: skipped item " + row.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
            }
            else Debug.LogWarning("LoadInventory: skipped slot " + row.slot + " for " + player.name + " because it's bigger than size " + player.inventorySize);
        }
    }

    void LoadEquipment(Player player)
    {
        // fill all slots first
        for (int i = 0; i < player.equipmentInfo.Length; ++i)
            player.equipment.Add(new ItemSlot());

        // then load valid equipment and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        foreach (character_equipment row in connection.Query<character_equipment>("SELECT * FROM character_equipment WHERE character=?", player.name))
        {
            if (row.slot < player.equipmentInfo.Length)
            {
                if (ScriptableItem.dict.TryGetValue(row.name.GetStableHashCode(), out ScriptableItem itemData))
                {
                    Item item = new Item(itemData);
                    item.summonedHealth = row.summonedHealth;
                    item.summonedLevel = row.summonedLevel;
                    item.summonedExperience = row.summonedExperience;
                    player.equipment[row.slot] = new ItemSlot(item, row.amount);
                }
                else Debug.LogWarning("LoadEquipment: skipped item " + row.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
            }
            else Debug.LogWarning("LoadEquipment: skipped slot " + row.slot + " for " + player.name + " because it's bigger than size " + player.equipmentInfo.Length);
        }
    }

    void LoadSkills(Player player)
    {
        // load skills based on skill templates (the others don't matter)
        // -> this way any skill changes in a prefab will be applied
        //    to all existing players every time (unlike item templates
        //    which are only for newly created characters)

        // fill all slots first
        foreach (ScriptableSkill skillData in player.skillTemplates)
            player.skills.Add(new Skill(skillData));

        // then load learned skills and put into their slots
        // (one big query is A LOT faster than querying each slot separately)
        foreach (character_skills row in connection.Query<character_skills>("SELECT * FROM character_skills WHERE character=?", player.name))
        {
            int index = player.skills.FindIndex(skill => skill.name == row.name);
            if (index != -1)
            {
                Skill skill = player.skills[index];
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                skill.level = Mathf.Clamp(row.level, 1, skill.maxLevel);
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                // castTimeEnd and cooldownEnd are based on NetworkTime.time
                // which will be different when restarting a server, hence why
                // we saved them as just the remaining times. so let's convert
                // them back again.
                skill.castTimeEnd = row.castTimeEnd + NetworkTime.time;
                skill.cooldownEnd = row.cooldownEnd + NetworkTime.time;

                player.skills[index] = skill;
            }
        }
    }

    void LoadBuffs(Player player)
    {
        // load buffs
        // note: no check if we have learned the skill for that buff
        //       since buffs may come from other people too
        foreach (character_buffs row in connection.Query<character_buffs>("SELECT * FROM character_buffs WHERE character=?", player.name))
        {
            if (ScriptableSkill.dict.TryGetValue(row.name.GetStableHashCode(), out ScriptableSkill skillData))
            {
                // make sure that 1 <= level <= maxlevel (in case we removed a skill
                // level etc)
                int level = Mathf.Clamp(row.level, 1, skillData.maxLevel);
                Buff buff = new Buff((BuffSkill)skillData, level);
                // buffTimeEnd is based on NetworkTime.time, which will be
                // different when restarting a server, hence why we saved
                // them as just the remaining times. so let's convert them
                // back again.
                buff.buffTimeEnd = row.buffTimeEnd + NetworkTime.time;
                player.buffs.Add(buff);
            }
            else Debug.LogWarning("LoadBuffs: skipped buff " + row.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
        }
    }

    void LoadQuests(Player player)
    {
        // load quests
        foreach (character_quests row in connection.Query<character_quests>("SELECT * FROM character_quests WHERE character=?", player.name))
        {
            ScriptableQuest questData;
            if (ScriptableQuest.dict.TryGetValue(row.name.GetStableHashCode(), out questData))
            {
                Quest quest = new Quest(questData);
                quest.progress = row.progress;
                quest.completed = row.completed;
                player.quests.Add(quest);
            }
            else Debug.LogWarning("LoadQuests: skipped quest " + row.name + " for " + player.name + " because it doesn't exist anymore. If it wasn't removed intentionally then make sure it's in the Resources folder.");
        }
    }

    // only load guild when their first player logs in
    // => using NetworkManager.Awake to load all guilds.Where would work,
    //    but we would require lots of memory and it might take a long time.
    // => hooking into player loading to load guilds is a really smart solution,
    //    because we don't ever have to load guilds that aren't needed
    void LoadGuildOnDemand(Player player)
    {
        string guildName = connection.ExecuteScalar<string>("SELECT guild FROM character_guild WHERE character=?", player.name);
        if (guildName != null)
        {
            // load guild on demand when the first player of that guild logs in
            // (= if it's not in GuildSystem.guilds yet)
            if (!GuildSystem.guilds.ContainsKey(guildName))
            {
                Guild guild = LoadGuild(guildName);
                GuildSystem.guilds[guild.name] = guild;
                player.guild = guild;
            }
            // assign from already loaded guild
            else player.guild = GuildSystem.guilds[guildName];
        }
    }

    public GameObject CharacterLoad(string characterName, List<Player> prefabs, bool isPreview)
    {
        characters row = connection.FindWithQuery<characters>("SELECT * FROM characters WHERE name=? AND deleted=0", characterName);
        if (row != null)
        {
            // instantiate based on the class name
            Player prefab = prefabs.Find(p => p.name == row.classname);
            if (prefab != null)
            {
                GameObject go = Instantiate(prefab.gameObject);
                Player player = go.GetComponent<Player>();

                player.name               = row.name;
                player.account            = row.account;
                player.className          = row.classname;
                Vector2 position          = new Vector2(row.x, row.y);
                player.level              = Mathf.Min(row.level, player.maxLevel); // limit to max level in case we changed it
                player.strength           = row.strength;
                player.intelligence       = row.intelligence;
                player.experience         = row.experience;
                player.skillExperience    = row.skillExperience;
                player.gold               = row.gold;
                player.coins              = row.coins;

                // is the position on a navmesh?
                // it might not be if we changed the terrain, or if the player
                // logged out in an instanced dungeon that doesn't exist anymore
                // NOTE: maxDist 0.1 is not enough in 2D. 1.0f works great.
                if (NavMesh2D.SamplePosition(position, out NavMeshHit2D hit, 1, NavMesh.AllAreas))
                {
                    // agent.warp is recommended over transform.position and
                    // avoids all kinds of weird bugs
                    player.agent.Warp(position);
                }
                // otherwise warp to start position
                else
                {
                    Transform start = NetworkManagerMMO.GetNearestStartPosition(position);
                    player.agent.Warp(start.position);
                    // no need to show the message all the time. it would spam
                    // the server logs too much.
                    //Debug.Log(player.name + " spawn position reset because it's not on a NavMesh anymore. This can happen if the player previously logged out in an instance or if the Terrain was changed.");
                }

                LoadInventory(player);
                LoadEquipment(player);
                LoadSkills(player);
                LoadBuffs(player);
                LoadQuests(player);
                LoadGuildOnDemand(player);

                // assign health / mana after max values were fully loaded
                // (they depend on equipment, buffs, etc.)
                player.health = row.health;
                player.mana = row.mana;

                // set 'online' directly. otherwise it would only be set during
                // the next CharacterSave() call, which might take 5-10 minutes.
                // => don't set it when loading previews though. only when
                //    really joining the world (hence setOnline flag)
                if (!isPreview)
                    connection.Execute("UPDATE characters SET online=1, lastsaved=? WHERE name=?", DateTime.UtcNow, characterName);

                // addon system hooks
                Utils.InvokeMany(typeof(Database), this, "CharacterLoad_", player);

                return go;
            }
            else Debug.LogError("no prefab found for class: " + row.classname);
        }
        return null;
    }

    void SaveInventory(Player player)
    {
        // inventory: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        connection.Execute("DELETE FROM character_inventory WHERE character=?", player.name);
        for (int i = 0; i < player.inventory.Count; ++i)
        {
            ItemSlot slot = player.inventory[i];
            if (slot.amount > 0) // only relevant items to save queries/storage/time
            {
                // note: .Insert causes a 'Constraint' exception. use Replace.
                connection.InsertOrReplace(new character_inventory{
                    character = player.name,
                    slot = i,
                    name = slot.item.name,
                    amount = slot.amount,
                    summonedHealth = slot.item.summonedHealth,
                    summonedLevel = slot.item.summonedLevel,
                    summonedExperience = slot.item.summonedExperience
                });
            }
        }
    }

    void SaveEquipment(Player player)
    {
        // equipment: remove old entries first, then add all new ones
        // (we could use UPDATE where slot=... but deleting everything makes
        //  sure that there are never any ghosts)
        connection.Execute("DELETE FROM character_equipment WHERE character=?", player.name);
        for (int i = 0; i < player.equipment.Count; ++i)
        {
            ItemSlot slot = player.equipment[i];
            if (slot.amount > 0) // only relevant equip to save queries/storage/time
            {
                connection.InsertOrReplace(new character_equipment{
                    character = player.name,
                    slot = i,
                    name = slot.item.name,
                    amount = slot.amount,
                    summonedHealth = slot.item.summonedHealth,
                    summonedLevel = slot.item.summonedLevel,
                    summonedExperience = slot.item.summonedExperience
                });
            }
        }
    }

    void SaveSkills(Player player)
    {
        // skills: remove old entries first, then add all new ones
        connection.Execute("DELETE FROM character_skills WHERE character=?", player.name);
        foreach (Skill skill in player.skills)
            if (skill.level > 0) // only learned skills to save queries/storage/time
            {
                // castTimeEnd and cooldownEnd are based on NetworkTime.time,
                // which will be different when restarting the server, so let's
                // convert them to the remaining time for easier save & load
                // note: this does NOT work when trying to save character data
                //       shortly before closing the editor or game because
                //       NetworkTime.time is 0 then.
                connection.InsertOrReplace(new character_skills{
                    character = player.name,
                    name = skill.name,
                    level = skill.level,
                    castTimeEnd = skill.CastTimeRemaining(),
                    cooldownEnd = skill.CooldownRemaining()
                });
            }
    }

    void SaveBuffs(Player player)
    {
        // buffs: remove old entries first, then add all new ones
        connection.Execute("DELETE FROM character_buffs WHERE character=?", player.name);
        foreach (Buff buff in player.buffs)
        {
            // buffTimeEnd is based on NetworkTime.time, which will be different
            // when restarting the server, so let's convert them to the
            // remaining time for easier save & load
            // note: this does NOT work when trying to save character data
            //       shortly before closing the editor or game because
            //       NetworkTime.time is 0 then.
            connection.InsertOrReplace(new character_buffs{
                character = player.name,
                name = buff.name,
                level = buff.level,
                buffTimeEnd = buff.BuffTimeRemaining()
            });
        }
    }

    void SaveQuests(Player player)
    {
        // quests: remove old entries first, then add all new ones
        connection.Execute("DELETE FROM character_quests WHERE character=?", player.name);
        foreach (Quest quest in player.quests)
        {
            connection.InsertOrReplace(new character_quests{
                character = player.name,
                name = quest.name,
                progress = quest.progress,
                completed = quest.completed
            });
        }
    }

    // adds or overwrites character data in the database
    public void CharacterSave(Player player, bool online, bool useTransaction = true)
    {
        // only use a transaction if not called within SaveMany transaction
        if (useTransaction) connection.BeginTransaction();

        connection.InsertOrReplace(new characters{
            name = player.name,
            account = player.account,
            classname = player.className,
            x = player.transform.position.x,
            y = player.transform.position.y,
            level = player.level,
            health = player.health,
            mana = player.mana,
            strength = player.strength,
            intelligence = player.intelligence,
            experience = player.experience,
            skillExperience = player.skillExperience,
            gold = player.gold,
            coins = player.coins,
            online = online,
            lastsaved = DateTime.UtcNow
        });

        SaveInventory(player);
        SaveEquipment(player);
        SaveSkills(player);
        SaveBuffs(player);
        SaveQuests(player);
        if (player.InGuild()) SaveGuild(player.guild, false); // TODO only if needs saving? but would be complicated

        // addon system hooks
        Utils.InvokeMany(typeof(Database), this, "CharacterSave_", player);

        if (useTransaction) connection.Commit();
    }

    // save multiple characters at once (useful for ultra fast transactions)
    public void CharacterSaveMany(IEnumerable<Player> players, bool online = true)
    {
        connection.BeginTransaction(); // transaction for performance
        foreach (Player player in players)
            CharacterSave(player, online, false);
        connection.Commit(); // end transaction
    }

    // guilds //////////////////////////////////////////////////////////////////
    public bool GuildExists(string guild)
    {
        return connection.FindWithQuery<guild_info>("SELECT * FROM guild_info WHERE name=?", guild) != null;
    }

    Guild LoadGuild(string guildName)
    {
        Guild guild = new Guild();

        // set name
        guild.name = guildName;

        // load guild info
        guild_info info = connection.FindWithQuery<guild_info>("SELECT * FROM guild_info WHERE name=?", guildName);
        if (info != null)
        {
            guild.notice = info.notice;
        }

        // load members list
        List<character_guild> rows = connection.Query<character_guild>("SELECT * FROM character_guild WHERE guild=?", guildName);
        GuildMember[] members = new GuildMember[rows.Count]; // avoid .ToList(). use array directly.
        for (int i = 0; i < rows.Count; ++i)
        {
            character_guild row = rows[i];

            GuildMember member = new GuildMember();
            member.name = row.character;
            member.rank = (GuildRank)row.rank;

            // is this player online right now? then use runtime data
            if (Player.onlinePlayers.TryGetValue(member.name, out Player player))
            {
                member.online = true;
                member.level = player.level;
            }
            else
            {
                member.online = false;
                // note: FindWithQuery<characters> is easier than ExecuteScalar<int> because we need the null check
                characters character = connection.FindWithQuery<characters>("SELECT * FROM characters WHERE name=?", member.name);
                member.level = character != null ? character.level : 1;
            }

            members[i] = member;
        }
        guild.members = members;
        return guild;
    }

    public void SaveGuild(Guild guild, bool useTransaction = true)
    {
        if (useTransaction) connection.BeginTransaction(); // transaction for performance

        // guild info
        connection.InsertOrReplace(new guild_info{
            name = guild.name,
            notice = guild.notice
        });

        // members list
        connection.Execute("DELETE FROM character_guild WHERE guild=?", guild.name);
        foreach (GuildMember member in guild.members)
        {
            connection.InsertOrReplace(new character_guild{
                character = member.name,
                guild = guild.name,
                rank = (int)member.rank
            });
        }

        if (useTransaction) connection.Commit(); // end transaction
    }

    public void RemoveGuild(string guild)
    {
        connection.BeginTransaction(); // transaction for performance
        connection.Execute("DELETE FROM guild_info WHERE name=?", guild);
        connection.Execute("DELETE FROM character_guild WHERE guild=?", guild);
        connection.Commit(); // end transaction
    }

    // item mall ///////////////////////////////////////////////////////////////
    public List<long> GrabCharacterOrders(string characterName)
    {
        // grab new orders from the database and delete them immediately
        //
        // note: this requires an orderid if we want someone else to write to
        // the database too. otherwise deleting would delete all the new ones or
        // updating would update all the new ones. especially in sqlite.
        //
        // note: we could just delete processed orders, but keeping them in the
        // database is easier for debugging / support.
        List<long> result = new List<long>();
        List<character_orders> rows = connection.Query<character_orders>("SELECT * FROM character_orders WHERE character=? AND processed=0", characterName);
        foreach (character_orders row in rows)
        {
            result.Add(row.coins);
            connection.Execute("UPDATE character_orders SET processed=1 WHERE orderid=?", row.orderid);
        }
        return result;
    }
}