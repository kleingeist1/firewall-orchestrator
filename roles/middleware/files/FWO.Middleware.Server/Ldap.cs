﻿using FWO.Logging;
using Novell.Directory.Ldap;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using FWO.Api.Data;

namespace FWO.Middleware.Server
{
    public class Ldap
    {
        // The following properties are retrieved from the database api:
        // ldap_server ldap_port ldap_search_user ldap_tls ldap_tenant_level ldap_connection_id ldap_search_user_pwd ldap_searchpath_for_users ldap_searchpath_for_roles    

        [JsonPropertyName("ldap_connection_id")]
        public int Id { get; set; }

        [JsonPropertyName("ldap_server")]
        public string Address { get; set; }

        [JsonPropertyName("ldap_port")]
        public int Port { get; set; }

        [JsonPropertyName("ldap_type")]
        public int Type { get; set; }

        [JsonPropertyName("ldap_pattern_length")]
        public int PatternLength { get; set; }

        [JsonPropertyName("ldap_search_user")]
        public string SearchUser { get; set; }

        [JsonPropertyName("ldap_tls")]
        public bool Tls { get; set; }

        [JsonPropertyName("ldap_tenant_level")]
        public int TenantLevel { get; set; }

        [JsonPropertyName("ldap_search_user_pwd")]
        public string SearchUserPwd { get; set; }

        [JsonPropertyName("ldap_searchpath_for_users")]
        public string UserSearchPath { get; set; }

        [JsonPropertyName("ldap_searchpath_for_roles")]
        public string RoleSearchPath { get; set; }

        [JsonPropertyName("ldap_searchpath_for_groups")]
        public string GroupSearchPath { get; set; }

        [JsonPropertyName("ldap_write_user")]
        public string WriteUser { get; set; }

        [JsonPropertyName("ldap_write_user_pwd")]
        public string WriteUserPwd { get; set; }

        [JsonPropertyName("tenant_id")]
        public int? TenantId { get; set; }

        private const int timeOutInMs = 3000;

        /// <summary>
        /// Builds a connection to the specified Ldap server.
        /// </summary>
        /// <returns>Connection to the specified Ldap server.</returns>
        private LdapConnection Connect()
        {
            try
            {
                LdapConnection connection = new LdapConnection { SecureSocketLayer = Tls, ConnectionTimeout = timeOutInMs };
                if (Tls) connection.UserDefinedServerCertValidationDelegate += (object sen, X509Certificate cer, X509Chain cha, SslPolicyErrors err) => true;  // todo: allow cert validation                
                connection.Connect(Address, Port);

                return connection;
            }

            catch (Exception exception)
            {
                throw new Exception($"Error while trying to reach LDAP server {Address}:{Port}", exception);
            }
        }

        public bool IsInternal()
        {
            return (WriteUser != null && WriteUser != "");
        }

        private string getUserSearchFilter(string searchPattern)
        {
            string userFilter;
            string searchFilter;
            if(Type == (int)LdapType.ActiveDirectory)
            {
                userFilter = "(&(objectclass=user)(!(objectclass=computer)))";
                searchFilter = $"(|(cn={searchPattern})(sAMAccountName={searchPattern}))";
            }
            else if(Type == (int)LdapType.OpenLdap)
            {
                userFilter = "(|(objectclass=user)(objectclass=person)(objectclass=inetOrgPerson)(objectclass=organizationalPerson))";
                searchFilter = $"(|(cn={searchPattern})(uid={searchPattern}))";
            }
            else // LdapType.Default
            {
                userFilter = "(&(|(objectclass=user)(objectclass=person)(objectclass=inetOrgPerson)(objectclass=organizationalPerson))(!(objectclass=computer)))";
                searchFilter = $"(|(cn={searchPattern})(uid={searchPattern})(userPrincipalName={searchPattern})(mail={searchPattern}))";
            }
            return ((searchPattern == null || searchPattern == "") ? userFilter : $"(&{userFilter}{searchFilter})");
        }

        private string getGroupSearchFilter(string searchPattern)
        {
            string groupFilter;
            string searchFilter;
            if(Type == (int)LdapType.ActiveDirectory)
            {
                groupFilter = "(objectClass=group)";
                searchFilter = $"(|(cn={searchPattern})(name={searchPattern}))";
            }
            else if(Type == (int)LdapType.OpenLdap)
            {
                groupFilter = "(|(objectclass=group)(objectclass=groupofnames)(objectclass=groupofuniquenames))";
                searchFilter = $"(cn={searchPattern})";
            }
            else // LdapType.Default
            {
                groupFilter = "(|(objectclass=group)(objectclass=groupofnames)(objectclass=groupofuniquenames))";
                searchFilter = $"(|(dc={searchPattern})(o={searchPattern})(ou={searchPattern})(cn={searchPattern})(uid={searchPattern})(mail={searchPattern}))";
            }
            return ((searchPattern == null || searchPattern == "") ? groupFilter : $"(&{groupFilter}{searchFilter})");
        }

        public string ValidateUser(UiUser user)
        {
            Log.WriteInfo("User Validation", $"Validating User: \"{user.Name}\" ...");
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as search user
                    connection.Bind(SearchUser, SearchUserPwd);
                    string[] attrList = new string[]{"*", "memberof"};

                    // Search for users in ldap with same name as user to validate
                    LdapSearchResults possibleUsers = (LdapSearchResults)connection.Search(
                        UserSearchPath,             // top-level path under which to search for user
                        LdapConnection.ScopeSub,    // search all levels beneath
                        getUserSearchFilter(user.Name),
     //                   $"(|(&(sAMAccountName={user.Name})(objectClass=person))(&(objectClass=inetOrgPerson)(uid:dn:={user.Name})))", // matching both AD and openldap filter
                        attrList,
                        typesOnly: false
                    );

                    while (possibleUsers.HasMore())
                    {
                        LdapEntry currentUser = possibleUsers.Next();
                      
                        try
                        {
                            Log.WriteDebug("User Validation", $"Trying to validate user with distinguished name: \"{ currentUser.Dn}\" ...");

                            // Try to authenticate as user with given password
                            connection.Bind(currentUser.Dn, user.Password);

                            // If authentication was successful (user is bound)
                            if (connection.Bound)
                            {
                                // Return ldap dn
                                Log.WriteDebug("User Validation", $"\"{ currentUser.Dn}\" successfully authenticated in {Address}.");
                                if (currentUser.GetAttributeSet().ContainsKey("mail"))
                                {
                                    user.Email = currentUser.GetAttribute("mail").StringValue;
                                }

                                // Simplest way as most ldap types should provide the memberof attribute.
                                // - Probably this doesn't work for nested groups.
                                // - Some systtems may only save the "primaryGroupID", then we would have to resolve the name.
                                // - Some others may force us to look into all groups to find the membership.
                                user.Groups = new List<string>();
                                foreach(var attribute in currentUser.GetAttributeSet())
                                {
                                    if (attribute.Name.ToLower() == "memberof")
                                    {
                                        foreach(string membership in attribute.StringValueArray)
                                        {
                                            if(membership.EndsWith(GroupSearchPath))
                                            {
                                                user.Groups.Add(membership);
                                            }
                                        }
                                    }
                                }
                                return currentUser.Dn;
                            }

                            else
                            {
                                // this will probably never be reached as an error is thrown before
                                // Incorrect password - do nothing, assume its another user with the same username
                                Log.WriteDebug($"User Validation {Address}", $"Found user with matching uid but different pwd: \"{ currentUser.Dn}\".");
                            }
                        }
                        catch (LdapException exc)
                        {
                            if (exc.ResultCode == 49)  // 49 = InvalidCredentials
                                Log.WriteDebug($"Duplicate user {Address}", $"Found user with matching uid but different pwd: \"{ currentUser.Dn}\".");
                            else
                                Log.WriteError($"Ldap exception {Address}", "Unexpected error while trying to validate user \"{ currentUser.Dn}\".");
                        } 
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to validate user", exception);
            }

            Log.WriteInfo("Invalid Credentials", $"Invalid login credentials - could not authenticate user \"{ user.Name}\" on {Address}:{Port}.");
            return "";
        }

        public string ChangePassword(string userDn, string oldPassword, string newPassword)
        {
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Try to authenticate as user with old password
                    connection.Bind(userDn, oldPassword);

                    if (connection.Bound)
                    {
                        // authentication was successful (user is bound): set new password
                        LdapAttribute attribute = new LdapAttribute("userPassword", newPassword);
                        LdapModification[] mods = { new LdapModification(LdapModification.Replace, attribute) };

                        connection.Modify(userDn, mods);
                        Log.WriteDebug("Change password", $"Password for user {userDn} changed in {Address}");
                    }
                    else
                    {
                        return "wrong old password";
                    }
                }
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
            return "";
        }

        public string SetPassword(string userDn, string newPassword)
        {
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);
                    if (connection.Bound)
                    {
                        // authentication was successful: set new password
                        LdapAttribute attribute = new LdapAttribute("userPassword", newPassword);
                        LdapModification[] mods = { new LdapModification(LdapModification.Replace, attribute) };

                        connection.Modify(userDn, mods);
                        Log.WriteDebug("Change password", $"Password for user {userDn} changed in {Address}");
                    }
                    else
                    {
                        return "error in write user authentication";
                    }
                }
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
            return "";
        }

        public string[] GetRoles(List<string> dnList)
        {
            return GetMemberships(dnList, RoleSearchPath).ToArray();
        }

        public List<string> GetGroups(List<string> dnList)
        {
            return GetMemberships(dnList, GroupSearchPath);
        }

        public List<string> GetMemberships(List<string> dnList, string searchPath)
        {
            List<string> userMemberships = new List<string>();

            // If this Ldap is containing roles / groups
            if (searchPath != null)
            {
                // Connect to Ldap
                using (LdapConnection connection = Connect())
                {     
                    // Authenticate as search user
                    connection.Bind(SearchUser, SearchUserPwd);

                    // Search for Ldap roles / groups in given directory          
                    int searchScope = LdapConnection.ScopeSub; // TODO: Correct search scope?
                    string searchFilter = $"(&(objectClass=groupOfUniqueNames)(cn=*))";
                    LdapSearchResults searchResults = (LdapSearchResults)connection.Search(searchPath, searchScope, searchFilter, null, false);                

                    // convert dnList to lower case to avoid case problems
                    dnList = dnList.ConvertAll(dn => dn.ToLower());

                    // Foreach found role / group
                    foreach (LdapEntry entry in searchResults)
                    {
                        Log.WriteDebug("Ldap Roles/Groups", $"Try to get roles / groups from ldap entry {entry.GetAttribute("cn").StringValue}");

                        // Get dn of users having current role / group
                        LdapAttribute members = entry.GetAttribute("uniqueMember");
                        string[] memberDn = members.StringValueArray;

                        // Foreach user 
                        foreach (string currentDn in memberDn)
                        {
                            Log.WriteDebug("Ldap Roles/Groups", $"Checking if current Dn: \"{currentDn}\" is user Dn. Then user has current role / group.");

                            // Check if current user dn is matching with given user dn => Given user has current role / group
                            if (dnList.Contains(currentDn.ToLower()))
                            {
                                // Get name and add it to list of roles / groups of given user
                                string name = entry.GetAttribute("cn").StringValue;
                                userMemberships.Add(name);
                                break;
                            }
                        }
                    }
                }
            }

            Log.WriteDebug($"Found the following roles / groups for user {dnList.FirstOrDefault()} in {Address}:", string.Join("\n", userMemberships));
            return userMemberships;
        }

        public List<KeyValuePair<string, List<KeyValuePair<string, string>>>> GetAllRoles()
        {
            List<KeyValuePair<string, List<KeyValuePair<string, string>>>> roleUsers = new List<KeyValuePair<string, List<KeyValuePair<string, string>>>>();

            // If this Ldap is containing roles
            if (RoleSearchPath != null)
            {
                // Connect to Ldap
                using (LdapConnection connection = Connect())
                {     
                    // Authenticate as search user
                    connection.Bind(SearchUser, SearchUserPwd);

                    // Search for Ldap roles in given directory          
                    int searchScope = LdapConnection.ScopeSub; // TODO: Correct search scope?
                    string searchFilter = $"(&(objectClass=groupOfUniqueNames)(cn=*))";
                    LdapSearchResults searchResults = (LdapSearchResults)connection.Search(RoleSearchPath, searchScope, searchFilter, null, false);                

                    // Foreach found role
                    foreach (LdapEntry entry in searchResults)
                    {
                        List<KeyValuePair<string, string>> attributes = new List<KeyValuePair<string, string>>();
                        string roleDesc = entry.GetAttribute("description").StringValue;
                        attributes.Add(new KeyValuePair<string, string>("description", roleDesc));

                        string[] roleMemberDn = entry.GetAttribute("uniqueMember").StringValueArray;
                        foreach (string currentDn in roleMemberDn)
                        {
                            if (currentDn != "")
                            {
                                attributes.Add(new KeyValuePair<string, string>("user", currentDn));
                            }
                        }
                        roleUsers.Add(new KeyValuePair<string, List<KeyValuePair<string, string>>>(entry.Dn, attributes));
                    }
                }
            }
            return roleUsers;
        }

        public List<string> GetAllGroups(string searchPattern)
        {
            List<string> allGroups = new List<string>();

            // Connect to Ldap
            using (LdapConnection connection = Connect())
            {     
                // Authenticate as search user
                connection.Bind(SearchUser, SearchUserPwd);

                // Search for Ldap groups in given directory          
                int searchScope = LdapConnection.ScopeSub;
                LdapSearchResults searchResults = (LdapSearchResults)connection.Search(GroupSearchPath, searchScope, getGroupSearchFilter(searchPattern), null, false);                

                foreach (LdapEntry entry in searchResults)
                {
                    allGroups.Add(entry.Dn);
                }
            }
            return allGroups;
        }

        public List<KeyValuePair<string, List<string>>> GetAllInternalGroups()
        {
            List<KeyValuePair<string, List<string>>> allGroups = new List<KeyValuePair<string, List<string>>>();

            // Connect to Ldap
            using (LdapConnection connection = Connect())
            {     
                // Authenticate as search user
                connection.Bind(SearchUser, SearchUserPwd);

                // Search for Ldap groups in given directory          
                int searchScope = LdapConnection.ScopeSub;
                LdapSearchResults searchResults = (LdapSearchResults)connection.Search(GroupSearchPath, searchScope, getGroupSearchFilter(""), null, false);                

                foreach (LdapEntry entry in searchResults)
                {
                    List<string> members = new List<string>();
                    string[] groupMemberDn = entry.GetAttribute("uniqueMember").StringValueArray;
                    foreach (string currentDn in groupMemberDn)
                    {
                        if (currentDn != "")
                        {
                            members.Add(currentDn);
                        }
                    }
                    allGroups.Add(new KeyValuePair<string, List<string>>(entry.Dn, members));
                }
            }
            return allGroups;
        }

        public List<KeyValuePair<string, string>> GetAllUsers(string searchPattern)
        {
            List<KeyValuePair<string, string>> allUsers = new List<KeyValuePair<string, string>>();

            // Connect to Ldap
            using (LdapConnection connection = Connect())
            {     
                // Authenticate as search user
                connection.Bind(SearchUser, SearchUserPwd);

                // Search for Ldap users in given directory          
                int searchScope = LdapConnection.ScopeSub;

                LdapSearchConstraints cons = connection.SearchConstraints;
                cons.ReferralFollowing = true;
                connection.Constraints = cons;

                LdapSearchResults searchResults = (LdapSearchResults)connection.Search(UserSearchPath, searchScope, getUserSearchFilter(searchPattern), null, false);                

                foreach (LdapEntry entry in searchResults)
                {
                    allUsers.Add(new KeyValuePair<string, string> (entry.Dn, (entry.GetAttributeSet().ContainsKey("mail") ? entry.GetAttribute("mail").StringValue : "")));
                }
            }
            return allUsers;
        }

        public bool AddUser(string userDn , string password, string email)
        {
            Log.WriteInfo("Add User", $"Trying to add User: \"{userDn}\"");
            bool userAdded = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    string userName = (new FWO.Api.Data.DistName(userDn)).UserName;
                    LdapAttributeSet attributeSet = new LdapAttributeSet();
                    attributeSet.Add( new LdapAttribute("objectclass", "inetOrgPerson"));
                    attributeSet.Add( new LdapAttribute("sn", userName));
                    attributeSet.Add( new LdapAttribute("cn", userName));
                    attributeSet.Add( new LdapAttribute("uid", userName));
                    attributeSet.Add( new LdapAttribute("userPassword", password));
                    attributeSet.Add( new LdapAttribute("mail", email));

                    LdapEntry newEntry = new LdapEntry( userDn, attributeSet );

                    try
                    {
                        //Add the entry to the directory
                        connection.Add(newEntry);
                        userAdded = true;
                        Log.WriteDebug("Add user", $"User {userName} added in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Add User", $"couldn't add user to LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to add user", exception);
            }
            return userAdded;
        }

        public bool UpdateUser(string userDn, string email)
        {
            Log.WriteInfo("Update User", $"Trying to update User: \"{userDn}\"");
            bool userUpdated = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    LdapAttribute attribute = new LdapAttribute("mail", email);
                    LdapModification[] mods = { new LdapModification(LdapModification.Replace, attribute) };

                    try
                    {
                        //Add the entry to the directory
                        connection.Modify(userDn, mods);
                        userUpdated = true;
                        Log.WriteDebug("Update user", $"User {userDn} updated in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Update User", $"couldn't update user in LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to update user", exception);
            }
            return userUpdated;
        }

        public bool DeleteUser(string userDn)
        {
            Log.WriteInfo("Delete User", $"Trying to delete User: \"{userDn}\"");
            bool userDeleted = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    try
                    {
                        //Delete the entry in the directory
                        connection.Delete(userDn);
                        userDeleted = true;
                        Log.WriteDebug("Delete user", $"User {userDn} deleted in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Delete User", $"couldn't delete user in LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to delete user", exception);
            }
            return userDeleted;
        }

        public string AddGroup(string groupName)
        {
            Log.WriteInfo("Add Group", $"Trying to add Group: \"{groupName}\"");
            bool groupAdded = false;
            string groupDn = "";
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    groupDn = $"cn={groupName},{GroupSearchPath}";
                    LdapAttributeSet attributeSet = new LdapAttributeSet();
                    attributeSet.Add( new LdapAttribute("objectclass", "groupofuniquenames"));
                    attributeSet.Add( new LdapAttribute("uniqueMember", ""));

                    LdapEntry newEntry = new LdapEntry( groupDn, attributeSet );

                    try
                    {
                        //Add the entry to the directory
                        connection.Add(newEntry);
                        groupAdded = true;
                        Log.WriteDebug("Add group", $"Group {groupName} added in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Add Group", $"couldn't add group to LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to add group", exception);
            }
            return (groupAdded ? groupDn : "");
        }

        public string UpdateGroup(string oldName, string newName)
        {
            Log.WriteInfo("Update Group", $"Trying to update Group: \"{oldName}\"");
            bool groupUpdated = false;
            string oldGroupDn = $"cn={oldName},{GroupSearchPath}";
            string newGroupRdn = $"cn={newName}";

            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    try
                    {
                        //Add the entry to the directory
                        connection.Rename(oldGroupDn, newGroupRdn, true);
                        groupUpdated = true;
                        Log.WriteDebug("Update group", $"Group {oldName} renamed to {newName} in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Update Group", $"couldn't update group in LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to update group", exception);
            }
            return (groupUpdated ? $"{newGroupRdn},{GroupSearchPath}" : "");
        }

        public bool DeleteGroup(string groupName)
        {
            Log.WriteInfo("Delete Group", $"Trying to delete Group: \"{groupName}\"");
            bool groupDeleted = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    try
                    {
                        //Delete the entry in the directory
                        string groupDn = $"cn={groupName},{GroupSearchPath}";
                        connection.Delete(groupDn);
                        groupDeleted = true;
                        Log.WriteDebug("Delete group", $"Group {groupName} deleted in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Delete Group", $"couldn't delete group in LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to delete group", exception);
            }
            return groupDeleted;
        }

        public bool AddUserToEntry(string userDn, string entry)
        {
            Log.WriteInfo("Add User to Entry", $"Trying to add User: \"{userDn}\" to Entry: \"{entry}\"");
            return ModifyUserInEntry(userDn, entry, LdapModification.Add);
        }
        
        public bool RemoveUserFromEntry(string userDn, string entry)
        {
            Log.WriteInfo("Remove User from Entry", $"Trying to remove User: \"{userDn}\" from Entry: \"{entry}\"");
            return ModifyUserInEntry(userDn, entry, LdapModification.Delete);
        }

        public bool RemoveUserFromAllEntries(string userDn)
        {
            List<string> dnList = new List<string>();
            dnList.Add(userDn); // group memberships do not need to be regarded here
            string[] roles = GetRoles(dnList);
            bool allRemoved = true;
            foreach(var role in roles)
            {
                allRemoved &= RemoveUserFromEntry(userDn, $"cn={role},{RoleSearchPath}");
            }
            if(GroupSearchPath != null && GroupSearchPath != "")
            {
                List<string> groups = GetGroups(dnList);
                foreach(var group in groups)
                {
                    allRemoved &= RemoveUserFromEntry(userDn, $"cn={group},{GroupSearchPath}");
                }
            }
            return allRemoved;
        }

        public bool ModifyUserInEntry(string userDn, string entry, int LdapModification)
        {
            bool userModified = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    // Add a new value to the description attribute
                    LdapAttribute attribute = new LdapAttribute("uniquemember", userDn);
                    LdapModification[] mods = { new LdapModification(LdapModification, attribute) }; 

                    try
                    {
                        //Modify the entry in the directory
                        connection.Modify (entry, mods);
                        userModified = true;
                        Log.WriteDebug("Modify Entry", $"Entry {entry} modified in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Modify Entry", $"maybe entry doesn't exist in this LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to modify user", exception);
            }
            return userModified;
        }

        public bool AddTenant(string tenantName)
        {
            Log.WriteInfo("Add Tenant", $"Trying to add Tenant: \"{tenantName}\"");
            bool tenantAdded = false;
            string tenantDn = "";
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    tenantDn = $"ou={tenantName},{UserSearchPath}";
                    LdapAttributeSet attributeSet = new LdapAttributeSet();
                    attributeSet.Add( new LdapAttribute("objectclass", "organizationalUnit"));

                    LdapEntry newEntry = new LdapEntry( tenantDn, attributeSet );

                    try
                    {
                        //Add the entry to the directory
                        connection.Add(newEntry);
                        tenantAdded = true;
                        Log.WriteDebug("Add tenant", $"Tenant {tenantName} added in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Add Tenant", $"couldn't add tenant to LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to add tenant", exception);
            }
            return tenantAdded;
        }

        public bool DeleteTenant(string tenantName)
        {
            Log.WriteInfo("Delete Tenant", $"Trying to delete Tenant: \"{tenantName}\"");
            bool tenantDeleted = false;
            try         
            {
                // Connecting to Ldap
                using (LdapConnection connection = Connect())
                {
                    // Authenticate as write user
                    connection.Bind(WriteUser, WriteUserPwd);

                    try
                    {
                        string tenantDn = "ou=" + tenantName + "," + UserSearchPath;

                        //Delete the entry in the directory
                        connection.Delete(tenantDn);
                        tenantDeleted = true;
                        Log.WriteDebug("Delete Tenant", $"tenant {tenantDn} deleted in {Address}");
                    }
                    catch(Exception exception)
                    {
                        Log.WriteInfo("Delete Tenant", $"couldn't delete tenant in LDAP {Address}: {exception.ToString()}");
                    }
                }
            }
            catch (Exception exception)
            {
                Log.WriteError($"Non-LDAP exception {Address}", "Unexpected error while trying to delete tenant", exception);
            }
            return tenantDeleted;
        }
    }
}
