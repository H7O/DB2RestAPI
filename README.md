# No-Code Database-to-REST API

A no-code solution designed to automatically convert your SQL queries into RESTful APIs—no API coding knowledge required.

If you can write basic SQL queries, this solution makes it easy to build safe, secure REST APIs in minutes.

It's designed to support a range of use cases out of the box: public APIs, B2B APIs with API key authentication, or full-stack applications with JWT/OIDC authentication. With built-in support for OAuth 2.0/OIDC providers (Azure B2C, Google, Auth0, and others), you can build complete front-end applications in React, Angular, or Vue that communicate directly with your database through secure, authenticated REST APIs.

If you value the DB-First approach, this solution offers a straightforward path to a production-ready REST API—without intermediary ORM layers, complex abstractions, proprietary query languages, or unnecessary GUI tooling.

Retain your SQL expertise and leverage it directly to build APIs in pure SQL.

> Note: For .NET developers looking to extend the solution with custom features, the codebase is fully accessible and ready to customize.


## How to use

1. Create a sample database and name it `test`, then run the SQL script below to create a sample `contacts` table within the `test` database that you just created.

> **Note**: Download and install either SQL Server Developer Edition or SQL Server Express if you don't have SQL Server installed on your machine

```sql
CREATE TABLE [dbo].[contacts] (
    [id]    UNIQUEIDENTIFIER CONSTRAINT [DEFAULT_contacts_id] DEFAULT (newid()) NOT NULL,
    [name]  NVARCHAR (500)   NULL,
    [phone] NVARCHAR (100)   NULL,
    [active] [bit] null DEFAULT 1,
    CONSTRAINT [PK_contacts] PRIMARY KEY CLUSTERED ([id] ASC)
);
```

2. Clone (or download) this repository.
3. Open the solution in Visual Studio.
4. Open the `/config/settings.xml` file and change the default `ConnectionStrings` to point to your `test` database.
5. Run the solution.
6. Download and install [Postman](https://www.postman.com/downloads/).
7. Open Postman and create a new request.
8. Set the request method to `POST` (or `GET`).
9. Set the request URL to `https://localhost:<your_custom_port>/hello_world` (e.g., https://localhost:7054/hello_world)
10. Fill `Content-Type` header with `application/json`.
11. Fill the request body with the following JSON:

```json
{
	"name": "John"
}
```
12. Send the request and you should see the following JSON respons: 
```json


    {
        "message_from_db": "hello John! Time now is 2025-10-21 04:47:18.373"
    }

```
13. To see how the API works, change the `name` property in the request body to `Jane` and send the request again. You should see a different response from the database.
14. To see the SQL query that generated the response, open the `/config/sql.xml` file and look for the `hello_world` query. You can change the query to anything you want and the API will still work as long as the query is valid and returns at least a single row.
15. If you examine the `hello_world` query in `/config/sql.xml`, you'll find the use of the `{{name}}` parameter. This parameter is passed from the request body to the query. You can add as many parameters as you want and use them in your queries.
```sql
        declare @name nvarchar(500) = {{name}};


        if (@name is null or ltrim(rtrim(@name)) = '')
        begin
            set @name = 'world';
        end
        select 
        'hello ' + @name + '! Time now is ' + convert(nvarchar(50), getdate(), 121) as message_from_db;
```

The full xml node in `sql.xml` that has the above query defined is as follows:
```xml
    <hello_world>
      <query>
        <![CDATA[
        
        declare @name nvarchar(500) = {{name}};


        if (@name is null or ltrim(rtrim(@name)) = '')
        begin
            set @name = 'world';
        end
        select 
        'hello ' + @name + '! Time now is ' + convert(nvarchar(50), getdate(), 121) as message_from_db;
        
        
        ]]>
      </query>

    </hello_world>
```

> **Note**: Notice that then name of the node `hello_world` is the API endpoint route we're calling in Postman. Later in further examples, we'll see how we can define custom routes to precicely control the route naming conventions instead of just having the xml node name to be the route name as the case in this example.

> **Note**: Passing parameters is safe and secure. The solution is designed to protect against SQL injection attacks by default via utilizing SQL Server's built-in parameterization feature. 
> The SQL parameterization feature is offered by `Com.H.Data.Common` package (available on [Github](https://github.com/H7O/Com.H.Data.Common) / [Nuget](https://www.nuget.org/packages/Com.H.Data.Common/)).


## Phonebook API examples

### Example 1 - Adding a contact record

Now, let's try to create a new record in the `contacts` table. 
1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts` and change the request method to `POST`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"name": "John",
	"phone": "1234567890"
}
```
4. Send the request and you should see the following JSON respons: 
```json
{
  "id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
  "name": "John",
  "phone": "1234567890",
  "active": 1
}

```

**About the `id` property:**
- Generated by the database and returned by the API—use it to update or delete the record later
- It's a `GUID` by default, but you can change it to `int` or `bigint` with `IDENTITY` for auto-increment
- The sample value above is just an example; your actual `id` will differ

5. To see how the API works, change the `name` and `phone` properties in the request body and send the request again. You should see a different response from the database.
6. Try adding multiple records with different names and phone numbers.
8. Try also adding the same name and phone number multiple times. 
You should get an error message from the database saying that the record already exists.
How this error is thrown from the database is up to you. 
The following XML tag in `/config/sql.xml` for `create_contact` illustrates how to throw an error from the database:
```xml
    <create_contact>
      <mandatory_parameters>name,phone</mandatory_parameters>
      <route>contacts</route>
      <verb>POST</verb>
      <success_status_code>201</success_status_code>

      <query>
      <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        declare @active bit = {{active}};
      
        -- check if the contact already exists
      
        declare @existing_contact table 
        (
            id UNIQUEIDENTIFIER,
            name nvarchar(500),
            phone nvarchar(100)
        );
        insert into @existing_contact select top 1 id, name, phone from [contacts] where name = @name and phone = @phone;
      
        declare @error_msg nvarchar(500);
      
        -- return an http 409 error (conflict error) if the contact already exists
      
        if ((select count(*) from [contacts] where name = @name and phone = @phone) > 0)
        begin 
            set @error_msg = 'Contact with name ' + @name + ' and phone ' + @phone + ' already exists';
            -- to return http error code `409 Conflict` throw 50409 and the app will return 409.
            -- same for other http error codes, e.g. 404, 500, etc. Just throw 50404, 50500, etc.
            throw 50409, @error_msg, 1;
            return;
        end
        if (@active is null)
        begin
            set @active = 1;
        end
      
      -- insert new contact, and return it back to the http client
      insert into [contacts] (id, name, phone, active) 
      output inserted.id, inserted.name, inserted.phone, inserted.active
      values (newid(), @name, @phone, @active)
    ]]>
      </query>

      
    </create_contact>
```
**XML Tag Reference:**

| Tag | Required | Default | Description |
|-----|----------|---------|-------------|
| `mandatory_parameters` | No | None | Comma-separated list of required parameters. Missing parameters return HTTP 400. |
| `route` | No | Node name | The API endpoint path (e.g., `contacts` → `/contacts`) |
| `verb` | No | Any | HTTP method(s) allowed (e.g., `POST`, `GET`, `PUT`, `DELETE`) |
| `success_status_code` | No | `200` | HTTP status code returned on success (e.g., `201` for Created) |
| `query` | **Yes** | — | SQL query wrapped in `<![CDATA[...]]>` to prevent XML parsing errors |

**Error Handling:**

- Throw error codes between `50000-51000` to return HTTP status codes `0-1000`
  - Example: `throw 50409` → HTTP `409 Conflict`
  - Example: `throw 50404` → HTTP `404 Not Found`
- Errors outside this range return HTTP `500` with a generic message to protect sensitive database information
- Customize the generic error message via the `generic_error_message` tag in `/config/settings.xml`

**Debugging SQL Errors:**

- By default, errors return a generic `500` message for security
- To see detailed SQL errors, send the `debug-mode` header with a value matching `debug_mode_header_value` in `/config/settings.xml`
- When enabled, error responses include the executed SQL query and full error message

### Example 2 - Updating a contact record

Now, let's try to update a record in the `contacts` table.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts/{id}` and change the request method to `PUT`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"name": "John Update 1",
	"phone": "1234567890"
}
```
4. Send the request and you should see the following JSON respons: 
```json

{
  "id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
  "name": "John Update 1",
  "phone": "1234567890",
  "active": 1
}

```
The response above shows the updated record.

1. To see how the API works, change the `name` and/or `phone` properties in the request body and send the request again. You should see a different response from the database.
2. Try updating the same record multiple times.
3. Try also updating a record that doesn't exist.
You should get an error message from the database saying that the record doesn't exist.
The error should be returned to the client as HTTP error code 404 (not found error).

Check the `/config/sql.xml` file for the `update_contact` node to see how the `id` parameter is used in the query.

Below is the `update_contact` node in the `/config/sql.xml` file:

```xml
<!-- Contact update endpoint -->
<update_contact>
  <route>contacts/{{id}}</route>
  <verb>PUT</verb>
  
  <mandatory_parameters>id,name,phone</mandatory_parameters>

  <connection_string_name>server2</connection_string_name>
  
  <query>
  <![CDATA[

  -- update contact
  declare @id UNIQUEIDENTIFIER = {{id}};
  declare @name nvarchar(500) = {{name}};
  declare @phone nvarchar(100) = {{phone}};
  
  -- check if contact exists
  
  declare @error_msg nvarchar(500);
  
  -- return an http 404 error (not found error) if the contact does not exist
  
  if ((select count(*) from [contacts] where id = @id) < 1)
  begin 
      set @error_msg = 'Contact with id ' + cast(@id as nvarchar(50)) + ' does not exist';
      -- to return http error code `404 Not found` throw 50404 and the app will return 404.
      throw 50404, @error_msg, 1;
      return;
  end
  
  -- update the contact, and return it back to the http client

  update [contacts] 
  set 
        [name] = @name, 
        phone = @phone 
  output 
    inserted.id, 
    inserted.name, 
    inserted.phone
  where 
    id = @id;      
      
      ]]>
      </query>
    </update_contact>
```

> **connection_string_name**
The `connection_string_name` tag is optional and used to specify the connection string name for the query.
The app is designed to pickup the `default` connection string you define in 
`/config/settings.xml` under `ConnectionStrings` node if you haven't set one here.
However, if you need to access a different database for a specific query,
you can set a different connection string name here, given that you've defined it in `/config/settings.xml` under `ConnectionStrings` node.


### Example 3 - Retrieving contact records along with the total number of records

Now, let's try to retrieve records from the `contacts` table along with the total number of records.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts` and change the request method to `GET`.
1. Fill `Content-Type` header with `application/json`.
1. Send the request and you should see the following JSON respons: 

```json
{
	"count": 3,
	"data": [
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
			"name": "John Update 1",
			"phone": "1234567890",
			"active": 1
		},
        {
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b1",
			"name": "John Update 2",
			"phone": "1234567890",
			"active": 1
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b2",
			"name": "John Update 3",
			"phone": "1234567890",
			"active": 1
		}
	]
}
```

The total number of records is returned in the `count` property in the response.

The records are returned in the `data` property in the response.


Check the `/config/sql.xml` file for the `search_contacts` node to see how the search parameters are used in the query.

```xml
    <!-- Contact search endpoint -->
    <search_contacts>
      <route>contacts</route>
      <verb>GET</verb>
      <query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        declare @take int = {{take}};
        declare @skip int = {{skip}};
        declare @sort_by nvarchar(50) = {{sort_by}};
        declare @sort_order nvarchar(10) = {{sort_order}};

        if (@sort_by is null or @sort_by = '')
        begin
            set @sort_by = 'name';
        end

        -- default take to 100 if not specified
        if (@take is null or @take < 1)
        begin
            set @take = 100;
        end
        -- make sure max take doesn't exceed 1000
        if (@take > 1000)
        begin
            set @take = 1000;
        end
        -- default skip to 0 if not specified
        if (@skip is null or @skip < 0)
        begin
            set @skip = 0;
        end
        
        
        if (@sort_by is null or @sort_by not in ('name', 'phone'))
        begin
            set @sort_by = 'name';
        end
        
        if (@sort_order is null or @sort_order not in ('asc', 'desc'))
        begin
            set @sort_order = 'asc';
        end


      select * from [contacts] 
        where 
          (@name is null or [name] like '%' +  @name + '%')
          and (@phone is null or [phone] like '%' +  @phone + '%')
        order by 
          case when @sort_by = 'name' and @sort_order = 'asc' then [name] end asc,
          case when @sort_by = 'name' and @sort_order = 'desc' then [name] end desc,
          case when @sort_by = 'phone' and @sort_order = 'asc' then [phone] end asc,
          case when @sort_by = 'phone' and @sort_order = 'desc' then [phone] end desc
        offset @skip rows
        fetch next @take rows only;        
        
        ]]>
      </query>
      <count_query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        select count(*) from [contacts] 
        where 
          (@name is null or [name] like '%' +  @name + '%')
          and (@phone is null or [phone] like '%' +  @phone + '%');
        
        ]]>
      </count_query>

    </search_contacts>
```
Notice how the above query has two nodes:
- The `query` node is used to return the actual records.
- The `count_query` node is used to return the total number of records that match the search criteria.
- The `count_query` node is optional, if you don't specify it, the app will not return the total count of the results.
- Both `query` and `count_query` nodes have much more functionality than what is shown in the above example. We'll cover them in the next examples.

### Example 4 - Pagination while retrieving contact records

In example 3, we saw how to retrieve records from the `contacts` table along with the total number of records.

Now, let's try to retrieve records from the `contacts` table along with the total number of records while also implementing pagination.

1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts` and change the request method to `GET`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"take": 3,
	"skip": 0
}
```
4. Send the request and you should see the following JSON respons: 
```json
{
	"count": 20,
	"data": [
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
			"name": "John",
			"phone": "1234567890",
			"active": 1
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b1",
			"name": "Jane",
			"phone": "3432345567",
			"active": 1
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b2",
			"name": "John Doe",
			"phone": "3425167890",
			"active": 1
		}
	]
}
```

The above response shows the first 3 records along with the total number of records that match the search criteria (i.e., our page size is 3 and we're on the first page).

To paginate through the records, you can change the `skip` parameter in the request body to skip a number of records.


Check the `/config/sql.xml` file for the `search_contacts` node to see how the `take` and `skip` parameters are used in the query.

### Example 5 - Searching while retrieving contact records

In example 4, we saw how to retrieve records from the `contacts` table along with the total number of records while also implementing pagination.

Now, let's try to retrieve records from the `contacts` table while also implementing searching.

1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts` and change the request method to `GET`.
2. Fill `Content-Type` header with `application/json`.
3. Fill the request body with the following JSON: 
```json
{
	"name": "j",
	"take": 3,
	"skip": 0
}
```
4. Send the request and you should see the following JSON respons:
```json
{
	"count": 20,
	"data": [
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
			"name": "John",
			"phone": "1234567890",
			"active": 1
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b1",
			"name": "Jane",
			"phone": "3432345567", 
			"active": 1
		},
		{
			"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b2",
			"name": "John Doe",
			"phone": "3425167890",
			"active": 1
		}
	]
}
```

The above response shows the first 3 records that match the search criteria (i.e., our page size is 3 and we're on the first page).

To paginate through the records, you can change the `skip` parameter in the request body to skip a number of records.

Check the `/config/sql.xml` file for the `search_contacts` node to see how the `name` parameter is used in the query.

You can also use the `phone` property as a search parameter. The API will return all records that contain the `phone` value in the `phone` column. The search is case-insensitive.

You can also use both `name` and `phone` properties as search parameters. The API will return all records that contain the `name` value in the `name` column and the `phone` value in the `phone` column. The search is case-insensitive.

### Example 6 - Deleting a contact record

Now, let's try to delete a record in the `contacts` table.
1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts/b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0` and change the request method to `DELETE`.
2. Fill `Content-Type` header with `application/json`.

> **Note**: The above is an example `id` value. You can use any `id` value that you get from the API when you add a new record or retrieve records.

3. Send the request and you should see the following JSON respons: 
```json
{
	"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
	"name": "John Update 1",
	"phone": "1234567890",
	"active": 1
}
```
> **Note**: If the record doesn't exist, you'll get an error message from the database saying that the record doesn't exist.
The error will be returned to the client as HTTP error code 404 (not found error).

Successfully deleted records are returned to the client as HTTP status code 204 (no content).

Check the `/config/sql.xml` file for the `delete_contact` node to see how the `id` parameter is used in the query.

Below is the `delete_contact` node in the `/config/sql.xml` file:

```xml
    <!-- Contact deletion endpoint -->
    <delete_contact>
      <route>contacts/{{id}}</route>
      <verb>DELETE</verb>
      <success_status_code>204</success_status_code>
      <mandatory_parameters>id</mandatory_parameters>
      <query>

        <![CDATA[
        declare @id UNIQUEIDENTIFIER = {{id}};
        -- check if contact exists
        declare @error_msg nvarchar(500);
        -- return an http 404 error (not found error) if the contact does not exist
        if ((select count(*) from [contacts] where id = @id) < 1)
        begin 
            set @error_msg = 'Contact with id ' + cast(@id as nvarchar(50)) + ' does not exist';
            -- to return http error code `404 Not found` throw 50404 and the app will return 404.
            throw 50404, @error_msg, 1;
            return;
        end
        -- delete the contact
        delete from [contacts] 
        OUTPUT DELETED.id, DELETED.name, DELETED.phone, DELETED.active
        where id = @id;
        
        ]]>
      </query>
    </delete_contact>

```

> **Note**: The `id` parameter is a mandatory parameter. If you don't specify it, the app will return an HTTP 400 error (bad request error).

The `verb` tag is set to `DELETE` to indicate that the request is a DELETE request. If the caller's http request is not a DELETE request, the app will return an HTTP 404 error (API endpoint not found error).

The `success_status_code` tag is set to `204` to indicate that the request was successful. If the request was successful, the app will return an HTTP 204 status code (no content).

### Example 8 - Activating / deactivating a contact record

Now, let's try to activate / deactivate a record in the `contacts` table.

1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts/b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0/deactivate` and change the request method to `PUT`.
2. Fill `Content-Type` header with `application/json`.
3. Send the request and you should see the following JSON respons: 
```json
{
	"id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
	"name": "John Update 1",
	"phone": "1234567890",
	"active": 0
}
```

The above response shows the record that was deactivated.

Check the `/config/sql.xml` file for the `activate_deactivate_contact` node to see how the `id` and `status_action` parameters are used in the query.

Below is the `activate_deactivate_contact` node in the `/config/sql.xml` file:

```xml
    <!-- Contact activation/deactivation endpoint -->
    <activate_deactivate_contact>
      <connection_string_name>server2</connection_string_name>
      <route>contacts/{{id}}/{{status_action}}</route>
      <verb>PUT</verb>
      <mandatory_parameters>id</mandatory_parameters>
      <query>
      <![CDATA[

      -- update contact
      declare @id UNIQUEIDENTIFIER = {{id}};
      -- status_action can be either `activate` or `deactivate`
      declare @status_action nvarchar(50) = {{status_action}};

      declare @error_msg nvarchar(500);

      if (@status_action is null or @status_action = ''
      or @status_action not in ('activate', 'deactivate'))
      begin
        set @error_msg = 'Invalid status action';
        throw 50400, @error_msg, 1;
        return;
      end
        
      -- check if contact exists
      
      -- return an http 404 error (not found error) if the contact does not exist
      
      if ((select count(*) from [contacts] where id = @id) < 1)
      begin 
          set @error_msg = 'Contact with id ' + cast(@id as nvarchar(50)) + ' does not exist';
          -- to return http error code `404 Not found` throw 50404 and the app will return 404.
          throw 50404, @error_msg, 1;
          return;
      end
      
      -- update the contact, and return it back to the http client

      declare @status_bit bit = case when @status_action = 'activate' then 1 else 0 end;

      update [contacts] 
      set 
            [active] = @status_bit
      output 
        inserted.id, 
        inserted.name, 
        inserted.phone,
        case when inserted.active = 1 then 'active' else 'inactive' end as status
      where 
        id = @id;      
      ]]>
      </query>
    </activate_deactivate_contact>
```
Notice the `{{action_status}}` parameter in the `route` tag.

The `{{action_status}}` parameter is used to specify the action status.

The `{{action_status}}` parameter can be either `activate` or `deactivate`.

This example showcase how you can create custom rountes that have multiple parameters. And how to use specific parameters for actions (like activate / deactivate in our case) that aren't limited by just the `verb` tag.


### Example 9 - Returning records without count

In example 3, we saw how to retrieve records from the `contacts` table along with the total number of records.

Now, let's try to retrieve records from the `contacts` table without the total number of records.

1. To do that, change the request URL to `https://localhost:<your_custom_port>/contacts_without_count` and change the request method to `GET`.
2. Fill `Content-Type` header with `application/json`.
3. Send the request and you should see the following JSON respons: 
```json
[
  {
    "id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b0",
    "name": "John Update 1",
    "phone": "1234567890",
    "active": 1
  },
  {
    "id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b1",
    "name": "Jane",
    "phone": "3432345567",
    "active": 1
  },
  {
    "id": "b0b0b0b0-b0b0-b0b0-b0b0-b0b0b0b0b0b2",
    "name": "John Doe",
    "phone": "3425167890",
    "active": 1
  }
]
```

Notice how the response is an array of JSON objects.

Check the `/config/sql.xml` file for the `search_contacts_without_count` node to see how the `response_structure` along with the absense of `count_query` tag is used to return the records without the total number of records.

Below is the `search_contacts_without_count` node in the `/config/sql.xml` file:

```xml
<!-- Contact search without count endpoint -->
    <search_contacts_without_count>
      <route>contacts_without_count</route>
      <verb>GET</verb>

      <!-- default value (if not specified) is `auto`, available options are `auto`, `single` and `array` -->
      <response_structure>array</response_structure>
      <!-- 
      `response_structure` is optional, if you don't specify it, the app defaults to `auto`.
       The possible values are:
       
      1- `array`: instructs the app to return an array of JSON objects for multiple rows response with the following rules:
        a) if the query returned a single row, that row is set to be returned as a json object inside an array
        b) if the query returned multiple rows, the app will return an array of JSON objects with the following structure:
          [
            {
              "id": 1,
              "name": "John",
              "phone": "1234567890"
            },
            {
              "id": 2,
              "name": "Jane",
              "phone": "0987654321"
            }
          ]
        
      2- `single`: instructs the app to return a single JSON object of the first row returned by the query and not iterate over the rest of the rows.
        however, if `count_query` node is specified, the app then is set to return a count structure as per the below structured
        response format but has only the first row returned by the query (i.e., not iterate over the rest of the rows)
      
      3- `auto`: for auto response format (single if single row, array if multiple rows)
      
      **Note**: if `count_query` is specified, the `response_structure` is then ignored as the app 
      is expected to always return a count structure as per the below structured response format:
          {
            "count": 1,
            "data": [
              {
                "id": 1,
                "name": "John",
                "phone": "1234567890"
              }
            ]
          }
      -->      

      <query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        declare @phone nvarchar(100) = {{phone}};
        declare @take int = {{take}};
        declare @skip int = {{skip}};
        declare @sort_by nvarchar(50) = {{sort_by}};
        declare @sort_order nvarchar(10) = {{sort_order}};

        if (@sort_by is null or @sort_by = '')
        begin
            set @sort_by = 'name';
        end

        -- default take to 100 if not specified
        if (@take is null or @take < 1)
        begin
            set @take = 100;
        end
        -- make sure max take doesn't exceed 1000
        if (@take > 1000)
        begin
            set @take = 1000;
        end
        -- default skip to 0 if not specified
        if (@skip is null or @skip < 0)
        begin
            set @skip = 0;
        end
        
        -- validate and default sort parameters (no need for sort by `id` if `id` is GUID, unless your `id` is an incrumental number)
        -- you can also add by create date, update date, etc. but for the purpose of this example, we'll only sort by `name` and `phone`
        if (@sort_by is null or @sort_by not in ('name', 'phone'))
        begin
            set @sort_by = 'name';
        end
        
        if (@sort_order is null or @sort_order not in ('asc', 'desc'))
        begin
            set @sort_order = 'asc';
        end


      select * from [contacts] 
        where 
          (@name is null or [name] like '%' +  @name + '%')
          and (@phone is null or [phone] like '%' +  @phone + '%')
        order by 
          case when @sort_by = 'name' and @sort_order = 'asc' then [name] end asc,
          case when @sort_by = 'name' and @sort_order = 'desc' then [name] end desc,
          case when @sort_by = 'phone' and @sort_order = 'asc' then [phone] end asc,
          case when @sort_by = 'phone' and @sort_order = 'desc' then [phone] end desc
        offset @skip rows
        fetch next @take rows only;        
        
        ]]>
      </query>

    </search_contacts_without_count>    
```

**response_structure**
The `response_structure` tag is optional and used to specify the response structure as being an array or a single object.

The possible values are:<br/>
**1-** `array`: instructs the app to return an array of JSON objects for multiple rows response with the following rules:<br/>
  - **a)** if the query returned a single row, that row is set to be returned as a json object inside an array<br/>
  - **b)** if the query returned multiple rows, the app will return an array of JSON objects with the following structure:<br/>
  ```json
  [
    {
      "id": 1,
      "name": "John",
      "phone": "1234567890",
      "active": 1
    },
    {
      "id": 2,
      "name": "Jane",
      "phone": "0987654321",
      "active": 1
    }
  ]
  ```        
**2-** `single`: instructs the app to return a single JSON object of the first row returned by the query and not iterate over the rest of the rows.<br/>
However, if `count_query` node is specified, the app then is set to return a count structure smiliar to the ones we saw in example 3, 4 and 5.

**3-** `auto`: for auto response format (single if single row, array if multiple rows)

> **Note**: if `count_query` is specified, the `response_structure` is then ignored as the app 
is expected to always return a count structure.

> **Note**: It's preferable to make use of the `response_structure` tag when you don't intend to use the `count_query` but expect to return multiple rows.<br/>
The reason for that is predictability. If you don't specify the `response_structure` tag, the app will default to `auto` which means that if the query returns a single row, the app will return a single object, and if the query returns multiple rows, the app will return an array of objects.<br/>
Callers of your API will have to handle both cases (single row and multiple rows) whereas if you specify the `response_structure` as `array` tag, the app will return an array of objects regardless of the number of rows returned by the query.<br/>

### Example 10 - Protecting your API from unauthorized access

You can protect your API endpoints from unauthorized access by using **API key collections**. This centralized approach allows you to define API keys once in `/config/api_keys.xml` and reference them from multiple endpoints.

#### Setting up API key collections

First, define your API key collections in `/config/api_keys.xml`:

```xml
<settings>
  <api_keys_collections>
    <external_vendors>
      <key>api key 1</key>
      <key>api key 2</key>
    </external_vendors>
    <internal_solutions>
      <key>api key 3</key>
    </internal_solutions>
  </api_keys_collections>
</settings>
```

In the above example:
- `external_vendors` and `internal_solutions` are collection names (you can name them whatever you like)
- Each collection contains one or more API keys
- The keys are automatically loaded and monitored for changes

#### Protecting an endpoint

To protect an endpoint, reference the collection name(s) in your API node in `/config/sql.xml`:

```xml
    <!-- Protected endpoint using API key collections -->
    <protected_hello_world>
      <api_keys_collections>external_vendors,internal_solutions</api_keys_collections>
      <query>
        <![CDATA[
        declare @name nvarchar(500) = {{name}};
        
        if (@name is null or ltrim(rtrim(@name)) = '')
        begin
            set @name = 'world';
        end
        select 'hello ' + @name + '!' as message_from_db;
        ]]>
      </query>
    </protected_hello_world>
```

The `api_keys_collections` tag accepts a comma-separated list of collection names. Any API key from any of the specified collections will be accepted.

Callers of your API must send the API key in the `x-api-key` http header.

#### Benefits of API key collections

✅ **Centralized management** - Define keys once, use them across multiple endpoints  
✅ **Flexible grouping** - Organize keys by vendor, client type, or any logical grouping  
✅ **Easy rotation** - Update keys in one place without modifying individual endpoints  
✅ **Automatic reload** - Changes to `api_keys.xml` are detected and applied automatically  

> **Note**: If you don't specify the `api_keys_collections` tag in your API node, the endpoint will be publicly accessible.



### Example 11 - Cached API responses

The solution offers the ability to cache API responses for a specified duration, enhancing performance and reducing latency for frequently accessed data.

To enable this feature, add `cache` node to any of your SQL queries in `sql.xml` file, as shown below:

```xml
    <hello_world_with_cache>
      <cache>
        <memory>
          <duration_in_milliseconds>20000</duration_in_milliseconds>
          <invalidators>name</invalidators>
        </memory>
      </cache>
      <query>
        <![CDATA[
        
        declare @name nvarchar(500) = {{name}};


        if (@name is null or ltrim(rtrim(@name)) = '')
        begin
            set @name = 'world';
        end
        select 'hello ' + @name + '! Time now is ' + convert(nvarchar(50), getdate(), 121) as message_from_db;
        
        
        ]]>
      </query>


    </hello_world_with_cache>

```

In the above example, the `hello_world_with_cache` query is configured to cache its response for 20 seconds. The `invalidators` node specifies the parameters that, when changed, will invalidate the cache and trigger a fresh query execution.

The response will be cached in memory for the specified duration, enhancing performance by eliminating the need to re-execute the query for subsequent requests within the cache's timeframe.

The `Time now is` part of the response is added to demonstrate that the response is indeed cached and not re-executed for subsequent requests within the cache's timeframe.

>**Usage Note:** To leverage the cached API response, simply call the API as usual. The solution will automatically cache the response based on the specified duration and invalidators.<br/>
Try changing the `name` parameter in the request body and sending the request multiple times within the cache's timeframe. You should see the same response with the same timestamp, indicating that the response is cached and not re-executed.

>**Note**: The `invalidators` node is optional and can be used to invalidate the cache when the specified parameters change.


### Example 12 - Nested JSON with SQL Server FOR JSON

When working with relational databases, you often need to return hierarchical or nested data structures. SQL Server's `FOR JSON PATH` clause allows you to generate JSON from SQL queries, but by default, SQL Server returns nested JSON as an escaped string rather than a proper JSON object.

This solution provides a **JSON type decorator** that automatically parses these JSON strings and embeds them as proper nested objects in your API response, eliminating the need for additional client-side parsing.

#### The problem

Consider this SQL query that returns contacts with their phone numbers using `FOR JSON PATH`:

```sql
SELECT
    name,
    (
        SELECT phone
        FROM contacts c2
        WHERE c2.name = c1.name AND c2.active = 1
        FOR JSON PATH
    ) AS phones,
    1 AS active
FROM contacts c1
WHERE c1.active = 1
GROUP BY name;
```

**Without the JSON type decorator**, SQL Server returns the `phones` field as an escaped JSON string:

```json
[
    {
        "name": "Bob Johnson",
        "phones": "[{\"phone\":\"+1-555-0103\"},{\"phone\":\"+1-555-0104\"}]",
        "active": 1
    },
    {
        "name": "Jane Smith",
        "phones": "[{\"phone\":\"+1-555-0102\"}]",
        "active": 1
    }
]
```

Notice how `phones` is a string containing escaped JSON characters (`\"`) rather than a proper JSON array.

Clients would need to call `JSON.parse()` on the `phones` field to work with it as an object:

```javascript
// Client-side workaround without decorator
const data = await fetch('/contacts').then(r => r.json());
data.forEach(contact => {
    contact.phones = JSON.parse(contact.phones); // Manual parsing required!
});
```

#### The solution: JSON type decorator

Add the `{type{json{field_name}}}` decorator to automatically parse JSON string fields into proper objects:

```xml
<nested_json>
  <query>
    <![CDATA[
      SELECT
          name,
          (
              SELECT phone
              FROM contacts c2
              WHERE c2.name = c1.name AND c2.active = 1
              FOR JSON PATH
          ) AS {type{json{phones}}},
          1 AS active
      FROM contacts c1
      WHERE c1.active = 1
      GROUP BY name;
    ]]>
  </query>
</nested_json>
```

**With the `{type{json{phones}}}` decorator**, the API automatically returns properly nested JSON:

```json
[
    {
        "name": "Bob Johnson",
        "phones": [
            {
                "phone": "+1-555-0103"
            },
            {
                "phone": "+1-555-0104"
            }
        ],
        "active": 1
    },
    {
        "name": "Jane Smith",
        "phones": [
            {
                "phone": "+1-555-0102"
            }
        ],
        "active": 1
    }
]
```

Now `phones` is a proper JSON array that clients can use directly without additional parsing!

```javascript
// Client-side code with decorator - no manual parsing needed!
const data = await fetch('/nested_json').then(r => r.json());
data.forEach(contact => {
    contact.phones.forEach(phoneObj => {
        console.log(phoneObj.phone); // Works directly!
    });
});
```

#### How it works

The `{type{json{field_name}}}` decorator tells the API engine to:
1. Recognize that the specified field contains a JSON string
2. Parse the JSON string during response serialization
3. Embed it as a proper nested object/array in the final response

This happens automatically on the server side, so your API consumers receive clean, properly structured JSON without any extra work.

#### Multiple nested fields

You can use the decorator for multiple fields in the same query:

```sql
SELECT
    name,
    (
        SELECT phone, type
        FROM phones
        WHERE contact_id = c.id
        FOR JSON PATH
    ) AS {type{json{phones}}},
    (
        SELECT street, city, country
        FROM addresses
        WHERE contact_id = c.id
        FOR JSON PATH
    ) AS {type{json{addresses}}},
    active
FROM contacts c
WHERE active = 1;
```

Both `phones` and `addresses` will be returned as proper JSON arrays in the response.

#### Benefits

✅ **Cleaner API responses** - Proper JSON structure without escaped strings  
✅ **Better client experience** - No manual `JSON.parse()` calls needed  
✅ **Type safety** - IDEs and TypeScript can infer proper types  
✅ **Reduced errors** - Eliminates parsing errors on the client side  
✅ **Performance** - Parsing happens once on the server instead of on every client  

> **Note**: This feature leverages the `Com.H.Text.Json` package which provides advanced JSON serialization capabilities including the type decorator syntax. The decorator works seamlessly with SQL Server's `FOR JSON PATH`, `FOR JSON AUTO`, and any other scenario where you need to embed JSON strings as proper objects.


### Example 13 - Acting as an API gateway

The solution also offers the feature of acting as an API gateway to seamlessly route requests to various other APIs.

This capability is helpful for consolidating a multitude of APIs under a single, unified base URL. 

Consumers benefit without needing to delve into the intricacies of where the data originates&mdash;be it from direct database calls or external APIs routed through the solution.

Moreover, in this setup, uniform API key enforcement becomes easy to manage. Whether interfacing with local resources or external services, the solution can enforce consistent API key usage, even if the external APIs don't inherently require such authentication.

To implement this functionality, we can utilize a setup file, `api_gateway.xml`, located within the config folder, to configure route mappings to different APIs.

Here's the structure of the `api_gateway.xml` file:

```xml
<settings>
  <routes>
    <cat_facts>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </cat_facts>
  </routes>
</settings>
```

The <routes> node contains a collection of customizable route configurations.

Consider the `cat_facts` route as an illustration:

```xml
<cat_facts>
	<url>https://catfact.ninja/fact</url>
	<excluded_headers>x-api-key,host</excluded_headers>
</cat_facts>
```

In this example:

* The `<cat_facts>` node denotes the route's name, which can be tailored as needed, this is the name you'll use to call the API (e.g., `https://localhost:<your_custom_port>/cat_facts`).
* The `<url>` node specifies the destination URL for routing the request.
* Optionally, the `<excluded_headers>` node allows exclusion of specific headers from the request before routing (i.e., `x-api-key,host` in this example won't be passed to the destination URL).

The app automatically directs requests to the designated external or local APIs based on the specified routes in `api_gateway.xml` and/or SQL queries outlined in `sql.xml`. Additionally, it automatically filters out the designated headers from the request before routing, optimizing compatibility and security.

By offering to exclude headers selectively, the solution safeguards against exposing sensitive data to external APIs while circumventing potential routing issues caused by extraneous headers. For instance, preemptively removing the `host` header prevents TLS handshake errors during routing, while omitting the `x-api-key` header shields our solution's API key from exposure to external services.

Optionally, the `ignore_certificate_errors` node permits bypassing certificate validation errors during API request routing, enhancing flexibility and compatibility.

### Example 14 - Protecting your API gateway routes from unauthorized access

You can protect your API gateway routes from unauthorized access by using the same **API key collections** system described in Example 10.

Simply reference the collection name(s) in your API gateway route configuration in `api_gateway.xml`:

```xml
    <!-- 
    Adds API key protection to the unprotected `catfact.ninja/fact` API
    before routing. Only clients with valid API keys from the specified
    collections can access this endpoint.
    -->
    <locally_protected_cat_facts>
      <api_keys_collections>external_vendors,internal_solutions</api_keys_collections>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </locally_protected_cat_facts>
```

With the above configuration:
- Callers must send a valid API key in the `x-api-key` http header
- The API key must exist in either the `external_vendors` or `internal_solutions` collection (defined in `/config/api_keys.xml`)
- The `x-api-key` header is excluded from being forwarded to the destination API for security

This approach allows you to add authentication to third-party APIs that don't have their own authentication, or to enforce your own API key standards across all routed services.

> **Note**: If you don't specify the `api_keys_collections` tag in your API gateway route, the route will be publicly accessible.

### Example 15 - Custom endpoint path

You can customize the endpoint path by adding the `route` tag to your API node in `api_gateway.xml` file just as we did with the `route` tag in `sql.xml` file.

Here is a sample of a custom endpoint path in `api_gateway.xml` file:

```xml
    <!-- 
    Callers to this API will use the path `cat/facts` (e.g., https://localhost:7054/cat/facts)
    will get routed to the `https://catfact.ninja/fact` API.
    -->
    <cat_facts_custom_path_example>
      <route>cat/facts/list</route>
      <url>https://catfact.ninja/fact</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </cat_facts_custom_path_example>
```

The above endpoint path will be accessible at `https://localhost:7054/cat/facts/list` and will be routed to the `https://catfact.ninja/fact` API.

### Example 16 - Wildcard route matching

You can use wildcard route matching to route multiple endpoints to single base URL by using the `*` wildcard character.

Here is a sample of a wildcard route matching in `api_gateway.xml` file:

```xml
    <cat_facts_wildcard_path_example>
      <route>cat/*</route>
      <url>https://catfact.ninja/</url>
      <excluded_headers>x-api-key,host</excluded_headers>
    </cat_facts_wildcard_path_example>
```

Any API call that starts with `cat/` will be routed to the `https://catfact.ninja/` API.

The remaining part of the path will be appended to the base URL.

For example, if you call `https://localhost:7054/cat/facts/list`, it will be routed to the `https://catfact.ninja/facts/list` API.

This is helpful when you have multiple endpoints that share the same base URL but have different paths. Instead of creating multiple routes, you can use a single route with a wildcard character to match the different paths.

> **Note**: The `*` wildcard character can be used to match any number of characters.

### Example 17 - Caching API gateway responses

Just like database queries, API gateway routes can also benefit from caching to improve performance and reduce load on target APIs.

The caching feature for API gateway routes works by storing the complete HTTP response (including status code, headers, and body) in memory for a specified duration. This is particularly useful for:
- Reducing latency for frequently accessed external APIs
- Protecting downstream services from high traffic
- Continuing to serve responses during temporary outages of target APIs
- Reducing costs when using metered external APIs

#### How it works

The cache key is automatically generated from:
- HTTP method (GET, POST, PUT, DELETE, etc.)
- Resolved route path (after wildcard matching)
- Query string parameters (from the caller)
- Request headers
- Configured invalidators

This means that different HTTP methods, different query parameters, or different header values will create separate cache entries, giving you granular control over what gets cached.

#### Configuration example

Add a `cache` section to your route in `/config/api_gateway.xml`:

```xml
<cat_facts_with_cache>
  <route>cat/facts</route>
  <url>https://catfact.ninja/fact</url>
  <excluded_headers>x-api-key,host</excluded_headers>

  <cache>
    <memory>
      <!-- Cache duration in milliseconds (20 seconds in this example) -->
      <duration_in_milliseconds>20000</duration_in_milliseconds>
      
      <!-- Parameters that invalidate cache (comma-separated) -->
      <!-- The system looks for these in both query parameters and headers -->
      <invalidators>category,limit</invalidators>
      
      <!-- Optional: Don't cache these HTTP status codes (comma-separated) -->
      <!-- By default, ALL status codes are cached (including errors) -->
      <!-- This protects your target API during outages or high traffic -->
      <exclude_status_codes_from_cache>401,403,429</exclude_status_codes_from_cache>
      
      <!-- Optional: Max size per invalidator value in characters (default: 1000) -->
      <max_per_value_cache_size>1000</max_per_value_cache_size>
    </memory>
  </cache>
</cat_facts_with_cache>
```

#### Testing the cache

1. Call the API endpoint twice with the same parameters:
```bash
# First call - response is fetched from target API and cached
curl https://localhost:7054/cat/facts

# Second call within 20 seconds - response served from cache (much faster!)
curl https://localhost:7054/cat/facts
```

2. Change the invalidator parameter to create a different cache entry:
```bash
# This creates a new cache entry because 'category' is different
curl https://localhost:7054/cat/facts?category=funny
```

3. Try different HTTP methods:
```bash
# GET and POST create separate cache entries even with same parameters
curl -X GET https://localhost:7054/cat/facts
curl -X POST https://localhost:7054/cat/facts
```

#### Cache invalidators

The `invalidators` configuration specifies which parameters should be included in the cache key. The system will look for these parameters in:
- **Query string parameters**: `?category=funny&limit=10`
- **Request headers**: `X-Tenant-Id`, `Authorization`, etc.

If a parameter specified in `invalidators` changes, a new cache entry is created.

**Example**: With `<invalidators>category,tenant_id</invalidators>`:
- `/cat/facts?category=funny` → Cache entry A
- `/cat/facts?category=sad` → Cache entry B (different category)
- `/cat/facts?category=funny` with header `X-Tenant-Id: 123` → Cache entry C (different tenant)

Parameters **not** listed in `invalidators` are ignored for caching purposes but still passed to the target API.

#### Status code filtering

By default, **all HTTP status codes are cached**, including errors like 404, 500, 502, etc. This design decision protects your target APIs during:
- High traffic periods
- Temporary outages
- Rate limiting scenarios

However, you can exclude specific status codes from being cached:

```xml
<!-- Don't cache authentication/authorization errors and rate limits -->
<exclude_status_codes_from_cache>401,403,429</exclude_status_codes_from_cache>
```

**Why cache error responses?**
- A 404 error during high traffic might indicate the target API is overwhelmed
- A 500 error might be temporary, and caching it reduces load on the failing service
- It gives the target API time to recover while still serving responses

**When to exclude status codes:**
- 401/403: Authentication errors that should be re-evaluated on each request
- 429: Rate limiting errors where you want to retry immediately
- Any status code where you need real-time verification

#### Performance considerations

**Without caching:**
- Every request is proxied to the target API
- Response is streamed directly to the client
- Optimal for single requests, but no protection during high traffic

**With caching:**
- First request: Response is buffered in memory and cached
- Subsequent requests (within cache duration): Served instantly from memory
- Expired/invalidated requests: Fall back to buffering and caching again

**Routes without cache configuration continue to stream responses directly** (backward compatible).

#### Complete example

Here's a practical example using a weather API:

```xml
<weather_with_cache>
  <route>weather/current</route>
  <url>https://api.weather.com/v1/current</url>
  <excluded_headers>x-api-key,host</excluded_headers>
  
  <!-- Override the API key header for the target API -->
  <applied_headers>
    <header>
      <name>X-API-Key</name>
      <value>your-weather-api-key-here</value>
    </header>
  </applied_headers>

  <cache>
    <memory>
      <!-- Cache for 5 minutes (weather doesn't change that often) -->
      <duration_in_milliseconds>300000</duration_in_milliseconds>
      
      <!-- Different cities and units create different cache entries -->
      <invalidators>city,units</invalidators>
      
      <!-- Don't cache authentication errors or service unavailable -->
      <exclude_status_codes_from_cache>401,403,503</exclude_status_codes_from_cache>
    </memory>
  </cache>
</weather_with_cache>
```

Usage:
```bash
# First call for London - hits the API, caches for 5 minutes
curl "https://localhost:7054/weather/current?city=London&units=metric"

# Subsequent calls for London within 5 minutes - served from cache
curl "https://localhost:7054/weather/current?city=London&units=metric"

# Different city - creates new cache entry
curl "https://localhost:7054/weather/current?city=Paris&units=metric"
```

> **Note**: The cache is stored in memory using ASP.NET Core's `HybridCache` feature, which provides high-performance caching with minimal overhead. All cached objects are automatically serialized and deserialized, allowing for efficient storage and retrieval.

> **Important**: Cache durations should be chosen based on how frequently the data changes and your performance requirements. Shorter durations mean more up-to-date data but more requests to the target API. Longer durations mean better performance but potentially stale data.


## File Upload Feature

The solution provides built-in support for file uploads with both `application/json` (base64-encoded files) and `multipart/form-data` content types. Files can be automatically stored in local file systems, SFTP servers, or both simultaneously.

### Prerequisites - File Stores Configuration

Before using file upload endpoints, you need to configure your file stores in `/config/file_management.xml`:

```xml
<settings>
  <file_management>
    <!-- Define the path structure for stored files -->
    <relative_file_path_structure>{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}</relative_file_path_structure>
    
    <!-- Global file restrictions (can be overridden per endpoint) -->
    <permitted_file_extensions>.txt,.pdf,.docx,.xlsx,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_number_of_files>5</max_number_of_files>
    <max_file_size_in_bytes>314572800</max_file_size_in_bytes> <!-- 300 MB -->
    
    <!-- File overwrite behavior (can be overridden per endpoint) -->
    <overwrite_existing_files>false</overwrite_existing_files> <!-- Prevents accidental overwrites -->
    
    <!-- Configure local file stores -->
    <local_file_store>
      <primary>
        <base_path><![CDATA[c:\myfiles\]]></base_path>
      </primary>
      <backup>
        <base_path><![CDATA[d:\backup\]]></base_path>
        <optional>true</optional> <!-- Won't fail request if this store fails -->
      </backup>
    </local_file_store>
    
    <!-- Configure SFTP file stores (you can have multiple) -->
    <sftp_file_store>
      <remote_storage>
        <host><![CDATA[sftp.example.com]]></host>
        <port>22</port>
        <username><![CDATA[your_username]]></username>
        <password><![CDATA[your_password]]></password>
        <base_path><![CDATA[/uploads/]]></base_path>
      </remote_storage>
      <offsite_backup>
        <host><![CDATA[backup.example.com]]></host>
        <port>2222</port>
        <username><![CDATA[backup_user]]></username>
        <password><![CDATA[backup_pass]]></password>
        <base_path><![CDATA[/backups/documents/]]></base_path>
        <optional>true</optional> <!-- Won't fail request if this store fails -->
      </offsite_backup>
    </sftp_file_store>
  </file_management>
</settings>
```

### Database Schema for File Metadata

Create a `files` table to store file metadata:

```sql
CREATE TABLE [dbo].[files] (
    [id]            UNIQUEIDENTIFIER NOT NULL,
    [contact_id]    UNIQUEIDENTIFIER NOT NULL,
    [file_name]     NVARCHAR(255) NOT NULL,
    [relative_path] NVARCHAR(1000) NOT NULL,
    [description]   NVARCHAR(1000) NULL,
    CONSTRAINT [PK_files] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [FK_files_contacts] FOREIGN KEY ([contact_id]) 
        REFERENCES [dbo].[contacts] ([id]) ON DELETE CASCADE
);
```

### Example - Creating a Contact with File Attachments

Let's create an endpoint that creates a contact record and attaches files (e.g., ID documents, photos):

**Configuration in `/config/sql.xml`:**

```xml
<create_contact_with_files>
  <mandatory_parameters>name,phone</mandatory_parameters>
  <route>v2/contacts</route>
  <verb>POST</verb>
  <success_status_code>201</success_status_code>
  
  <file_management>
    <!-- Reference the stores defined in file_management.xml -->
    <stores>primary, backup, remote_storage, offsite_backup</stores>
    
    <!-- Override global settings for this endpoint -->
    <permitted_file_extensions>.txt,.pdf,.png,.jpg,.jpeg</permitted_file_extensions>
    <max_file_size_in_bytes>10485760</max_file_size_in_bytes> <!-- 10 MB -->
    <max_number_of_files>3</max_number_of_files>
    
    <!-- Specify the JSON field/form field name for file metadata -->
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
    
    <!-- Set to false if you only need metadata in SQL (not file content) -->
    <pass_files_content_to_query>false</pass_files_content_to_query>
  </file_management>
  
  <query>
  <![CDATA[
    USE [test]
    
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    DECLARE @active BIT = ISNULL({{active}}, 1);
    DECLARE @files_json NVARCHAR(MAX) = {{attachments}};
    
    -- Check if contact already exists
    IF EXISTS (SELECT 1 FROM [contacts] WHERE name = @name AND phone = @phone)
    BEGIN
        DECLARE @error_msg NVARCHAR(500) = 'Contact already exists';
        THROW 50409, @error_msg, 1; -- Returns HTTP 409 Conflict
        RETURN;
    END
    
    -- Tables to hold results
    DECLARE @new_contact TABLE (
        id UNIQUEIDENTIFIER,
        name NVARCHAR(500),
        phone NVARCHAR(100),
        active BIT
    );
    
    DECLARE @files TABLE (
        id UNIQUEIDENTIFIER,
        contact_id UNIQUEIDENTIFIER,
        file_name NVARCHAR(255),
        relative_path NVARCHAR(1000),
        description NVARCHAR(1000)
    );
    
    BEGIN TRANSACTION;
    BEGIN TRY
        -- Insert new contact
        INSERT INTO [contacts] (id, name, phone, active)
        OUTPUT inserted.id, inserted.name, inserted.phone, inserted.active
        INTO @new_contact
        VALUES (NEWID(), @name, @phone, @active);
        
        -- Parse file metadata from JSON
        INSERT INTO @files (id, file_name, relative_path, description)
        SELECT 
            JSON_VALUE(value, '$.id'),
            JSON_VALUE(value, '$.file_name'),
            JSON_VALUE(value, '$.relative_path'),
            JSON_VALUE(value, '$.description')
        FROM OPENJSON(@files_json);
        
        -- Link files to contact
        UPDATE @files 
        SET contact_id = (SELECT id FROM @new_contact);
        
        -- Save file metadata to database
        INSERT INTO [files] (id, contact_id, file_name, relative_path, description)
        SELECT id, contact_id, file_name, relative_path, description
        FROM @files;
        
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        ROLLBACK TRANSACTION;
        THROW;
    END CATCH
    
    -- Return contact with attached files
    SELECT 
        nc.id,
        nc.name,
        nc.phone,
        nc.active,
        (
            SELECT id, file_name, relative_path, description
            FROM @files
            FOR JSON PATH
        ) AS {type{json{files}}}
    FROM @new_contact nc;
  ]]>
  </query>
</create_contact_with_files>
```

### Usage Examples

#### Method 1: Using `application/json` with Base64-Encoded Files

This method is ideal when you already have files as base64 strings (e.g., from a web form or mobile app):

**Request:**
```bash
POST https://localhost:7054/v2/contacts
Content-Type: application/json

{
  "name": "John Doe",
  "phone": "+1-555-0100",
  "active": true,
  "attachments": [
    {
      "file_name": "drivers_license.jpg",
      "base64_content": "/9j/4AAQSkZJRgABAQEAYABgAAD...",
      "description": "Driver's License - Front"
    },
    {
      "file_name": "proof_of_address.pdf",
      "base64_content": "JVBERi0xLjQKJeLjz9MKMyAwIG9iago8P...",
      "description": "Utility Bill"
    }
  ]
}
```

**Response:**
```json
{
  "id": "a3d5e7f9-1234-5678-90ab-cdef12345678",
  "name": "John Doe",
  "phone": "+1-555-0100",
  "active": true,
  "files": [
    {
      "id": "b1c2d3e4-5678-90ab-cdef-1234567890ab",
      "file_name": "drivers_license.jpg",
      "relative_path": "2025/Nov/11/b1c2d3e4-5678-90ab-cdef-1234567890ab/drivers_license.jpg",
      "description": "Driver's License - Front"
    },
    {
      "id": "c2d3e4f5-6789-01bc-def1-234567890abc",
      "file_name": "proof_of_address.pdf",
      "relative_path": "2025/Nov/11/c2d3e4f5-6789-01bc-def1-234567890abc/proof_of_address.pdf",
      "description": "Utility Bill"
    }
  ]
}
```

#### Method 2: Using `multipart/form-data`

This method is better for traditional file uploads from HTML forms or when dealing with large files:

**Request:**
```bash
POST https://localhost:7054/v2/contacts
Content-Type: multipart/form-data; boundary=----WebKitFormBoundary7MA4YWxkTrZu0gW

------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="name"

John Doe
------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="phone"

+1-555-0100
------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="active"

true
------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="attachments"
Content-Type: application/json

[
  {
    "file_name": "drivers_license.jpg",
    "description": "Driver's License - Front"
  },
  {
    "file_name": "proof_of_address.pdf",
    "description": "Utility Bill"
  }
]
------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="file"; filename="drivers_license.jpg"
Content-Type: image/jpeg

[binary file content]
------WebKitFormBoundary7MA4YWxkTrZu0gW
Content-Disposition: form-data; name="file"; filename="proof_of_address.pdf"
Content-Type: application/pdf

[binary file content]
------WebKitFormBoundary7MA4YWxkTrZu0gW--
```

**Using Postman:**
1. Set request method to `POST` and URL to `https://localhost:7054/v2/contacts`
2. Select the **Body** tab
3. Select **form-data** radio button
4. Add text fields: `name`, `phone`, `active`
5. Add a `attachments` field with value:
   ```json
   [
     {"file_name": "drivers_license.jpg", "description": "Driver's License - Front"},
     {"file_name": "proof_of_address.pdf", "description": "Utility Bill"}
   ]
   ```
6. Add file fields by changing dropdown from "Text" to "File" and select files

### How It Works

1. **File Upload**: When you send the request, files are temporarily stored in the system's temp directory
2. **Validation**: The system validates file extensions, sizes, and counts against your configuration
3. **Storage**: Files are copied to all configured stores (local and/or SFTP) in parallel
4. **Metadata Generation**: The system generates:
   - A unique GUID for each file (if not provided)
   - Relative path based on the configured structure (e.g., `2025/Nov/11/{guid}/filename.ext`)
   - File extension and size information
5. **SQL Processing**: Your SQL query receives the enriched metadata as JSON
6. **Cleanup**: Temporary files are automatically cleaned up after the request completes
7. **Transaction Safety**: If any mandatory store fails, the entire transaction rolls back

### Advanced Features

**Optional Stores**: Mark stores (local or SFTP) as optional to prevent failures if a secondary storage location is unavailable:
```xml
<local_file_store>
  <backup>
    <base_path><![CDATA[d:\backup\]]></base_path>
    <optional>true</optional> <!-- Request won't fail if this store is unavailable -->
  </backup>
</local_file_store>
```

**Custom File Path Structure**: Customize how files are organized using date patterns and GUIDs:
```xml
<relative_file_path_structure>{date{yyyy}}/{date{MMM}}/{date{dd}}/{{guid}}/{file{name}}</relative_file_path_structure>
<!-- Results in: 2025/Nov/11/a3d5e7f9-1234-5678-90ab-cdef12345678/document.pdf -->
```

**Pass File Content to SQL**: If you need file content in your SQL query (e.g., for virus scanning or database storage):
```xml
<pass_files_content_to_query>true</pass_files_content_to_query>
```

This adds a `base64_content` field to the JSON passed to your query.

**Custom Field Names**: Match your existing API contracts:
```xml
<filename_field_in_payload>fileName</filename_field_in_payload>
<base64_content_field_in_payload>fileContent</base64_content_field_in_payload>
<files_json_field_or_form_field_name>documents</files_json_field_or_form_field_name>
```

**Overwrite Existing Files**: Control whether uploading a file with the same path should overwrite an existing file:
```xml
<!-- In file_management.xml (global setting) -->
<overwrite_existing_files>false</overwrite_existing_files>

<!-- Or per-endpoint in sql.xml -->
<file_management>
    <stores>primary</stores>
    <overwrite_existing_files>true</overwrite_existing_files> <!-- Allow overwrites for this endpoint -->
</file_management>
```

When `overwrite_existing_files` is `false` (the default):
- Attempting to upload a file to a path that already exists will throw an error
- Error message: `"File 'path/to/file.pdf' already exists in store 'primary'. Set 'overwrite_existing_files' to true to allow overwriting."`
- This applies to both local file stores and SFTP stores
- Useful for preventing accidental data loss

When `overwrite_existing_files` is `true`:
- Existing files at the destination path will be silently replaced
- No error or warning is generated
- Useful for update scenarios where you intentionally want to replace files

> **Priority**: Endpoint-specific settings override global settings. If not specified anywhere, defaults to `false`.

### Engine-Enriched File Properties

When files are processed, the engine enriches each file object with additional properties before passing to your SQL query:

| Property | Type | Description |
|----------|------|-------------|
| `id` | GUID | Unique file identifier (generated or caller-provided if `accept_caller_defined_file_ids` is true) |
| `file_name` | string | Original filename (validated and normalized) |
| `relative_path` | string | Storage path based on `relative_file_path_structure` |
| `extension` | string | File extension (e.g., `.pdf`) |
| `mime_type` | string | MIME type (e.g., `application/pdf`) |
| `size` | number | File size in bytes |
| `is_new_upload` | boolean | `true` if this file was actually uploaded, `false`/absent for existing file metadata passed through |
| `backend_temp_file_path` | string | Temp file path (only present when `pass_files_content_to_query` is false and file was uploaded) |

### Partial File Updates (Update Records with Existing Files)

When updating a record that already has associated files, you often need to:
- **Keep existing files** - Include their metadata (with `id`) but don't re-upload the binary
- **Add new files** - Include new files with binary content
- **Remove files** - Simply don't include them in the files array
- **Update metadata only** - Change document_type, remarks, etc. without re-uploading

#### Configuration Required

Enable `accept_caller_defined_file_ids` for update endpoints so the engine preserves file IDs sent by the caller:

```xml
<file_management>
    <stores>primary</stores>
    <accept_caller_defined_file_ids>true</accept_caller_defined_file_ids>
    <!-- other settings... -->
</file_management>
```

#### Example: Update with Partial File Changes

Assume a contact has 3 existing documents (A, B, D). You want to:
- Keep document A unchanged
- Update document B's description  
- Add a new document C
- Remove document D (by not including it)

**JSON Request:**
```json
PUT /v2/contacts/123e4567-e89b-12d3-a456-426614174000
Content-Type: application/json

{
  "name": "John Doe Updated",
  "attachments": [
    {
      "id": "aaaa-existing-doc-a-id",
      "file_name": "passport.pdf",
      "document_type": "Passport"
    },
    {
      "id": "bbbb-existing-doc-b-id", 
      "file_name": "emirates_id.jpg",
      "document_type": "Emirates ID",
      "description": "Updated description for doc B"
    },
    {
      "file_name": "new_certificate.pdf",
      "document_type": "Certificate",
      "description": "Newly added document",
      "base64_content": "JVBERi0xLjQK..."
    }
  ]
}
```

**Multipart Request:**
```http
PUT /v2/contacts/123e4567-e89b-12d3-a456-426614174000
Content-Type: multipart/form-data; boundary=----FormBoundary

------FormBoundary
Content-Disposition: form-data; name="name"

John Doe Updated
------FormBoundary
Content-Disposition: form-data; name="attachments"

[
  {"id": "aaaa-existing-doc-a-id", "file_name": "passport.pdf", "document_type": "Passport"},
  {"id": "bbbb-existing-doc-b-id", "file_name": "emirates_id.jpg", "document_type": "Emirates ID", "description": "Updated description"},
  {"file_name": "new_certificate.pdf", "document_type": "Certificate", "description": "Newly added"}
]
------FormBoundary
Content-Disposition: form-data; name="file"; filename="new_certificate.pdf"
Content-Type: application/pdf

(binary content for new file only)
------FormBoundary--
```

> **Key Point:** Only include binary content for NEW files. Existing files (those without `base64_content` in JSON mode, or without a matching binary upload in multipart mode) are passed through with `is_new_upload = false`.

#### SQL Query for Partial Updates

Use the `is_new_upload` field to differentiate between new and existing files:

```xml
<update_contact_with_files>
  <route>v2/contacts/{{id}}</route>
  <verb>PUT</verb>
  <mandatory_parameters>id</mandatory_parameters>
  
  <file_management>
    <stores>primary</stores>
    <accept_caller_defined_file_ids>true</accept_caller_defined_file_ids>
    <files_json_field_or_form_field_name>attachments</files_json_field_or_form_field_name>
  </file_management>
  
  <query>
  <![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @files NVARCHAR(MAX) = {{attachments}};
    
    -- Update contact fields
    UPDATE [contacts]
    SET name = COALESCE(@name, name),
        m_date = GETDATE()
    WHERE id = @id;
    
    -- Process files if provided
    IF @files IS NOT NULL
    BEGIN
        -- Collect all file IDs being sent (both existing and new)
        DECLARE @sent_ids TABLE (id UNIQUEIDENTIFIER);
        INSERT INTO @sent_ids (id)
        SELECT TRY_CAST(JSON_VALUE(value, '$.id') AS UNIQUEIDENTIFIER)
        FROM OPENJSON(@files)
        WHERE JSON_VALUE(value, '$.id') IS NOT NULL;
        
        -- DELETE: Remove files not in the sent list
        DELETE FROM [files] 
        WHERE contact_id = @id 
          AND id NOT IN (SELECT id FROM @sent_ids);
        
        -- UPDATE: Existing files (is_new_upload is false or not present)
        -- Only update metadata like description, document_type, etc.
        UPDATE f
        SET 
            f.description = COALESCE(JSON_VALUE(j.value, '$.description'), f.description),
            f.document_type = COALESCE(JSON_VALUE(j.value, '$.document_type'), f.document_type),
            f.m_date = GETDATE()
        FROM [files] f
        INNER JOIN OPENJSON(@files) j 
            ON TRY_CAST(JSON_VALUE(j.value, '$.id') AS UNIQUEIDENTIFIER) = f.id
        WHERE f.contact_id = @id
          AND COALESCE(JSON_VALUE(j.value, '$.is_new_upload'), 'false') != 'true';
        
        -- INSERT: New files (is_new_upload = true)
        INSERT INTO [files] (id, contact_id, file_name, relative_path, mime_type, size, description, document_type, c_date)
        SELECT 
            TRY_CAST(JSON_VALUE(value, '$.id') AS UNIQUEIDENTIFIER),
            @id,
            JSON_VALUE(value, '$.file_name'),
            JSON_VALUE(value, '$.relative_path'),
            JSON_VALUE(value, '$.mime_type'),
            TRY_CAST(JSON_VALUE(value, '$.size') AS BIGINT),
            JSON_VALUE(value, '$.description'),
            JSON_VALUE(value, '$.document_type'),
            GETDATE()
        FROM OPENJSON(@files)
        WHERE JSON_VALUE(value, '$.is_new_upload') = 'true';
    END
    
    -- Return updated record with files
    SELECT 
        c.id, c.name, c.phone,
        (SELECT id, file_name, relative_path, description, document_type
         FROM [files] WHERE contact_id = c.id
         FOR JSON PATH) AS {type{json{files}}}
    FROM [contacts] c
    WHERE c.id = @id;
  ]]>
  </query>
</update_contact_with_files>
```

#### Summary Table

| Scenario | Include ID? | Include Binary? | `is_new_upload` value |
|----------|-------------|-----------------|----------------------|
| Keep existing file unchanged | ✅ Yes | ❌ No | `false` (or absent) |
| Update existing file's metadata | ✅ Yes | ❌ No | `false` (or absent) |
| Add new file | ❌ No (engine generates) | ✅ Yes | `true` |
| Remove file | Don't include in array | - | - |

> **Security Note**: When `accept_caller_defined_file_ids` is `true`, always validate in your SQL query that the provided file IDs actually belong to the record being updated. This prevents unauthorized access to files from other records.

> **Security Note**: The file upload feature includes built-in protection against path traversal attacks and validates file extensions to prevent malicious uploads. Always configure `permitted_file_extensions` to whitelist only the file types your application needs.

> **Performance Note**: File uploads are processed asynchronously with optimized I/O operations. Large files are streamed rather than loaded entirely into memory. For production deployments, consider implementing virus scanning and additional validation in your SQL procedures.


## File Download Feature

The solution provides seamless file download capabilities with support for multiple storage sources: database storage (base64), local file systems, SFTP servers, and HTTP/HTTPS URLs. Files are streamed efficiently to handle large downloads without consuming excessive memory.

### How File Downloads Work

When a request is made to a download endpoint:
1. Your SQL query returns metadata about the file (name, location, content type)
2. The system determines the file source (database, local store, SFTP, or HTTP)
3. The file is streamed directly to the client with appropriate headers
4. Large files are handled efficiently with async streaming

### File Source Priority

If multiple sources are provided, the system prioritizes in this order:
1. **`base64_content`** - File content stored in the database
2. **`relative_path`** - File stored in a file store (local or SFTP)
3. **`http`** - File proxied from an HTTP/HTTPS URL

### Example - Document Download Endpoint

Let's create an endpoint to download files that were uploaded with the contact records:

**Configuration in `/config/sql.xml`:**

```xml
<download_document>
  <route>documents/{{id}}</route>
  <verb>GET</verb>
  <mandatory_parameters>id</mandatory_parameters>
  
  <!-- Setting response_structure to 'file' enables file download mode -->
  <response_structure>file</response_structure>
  
  <file_management>
    <!-- Specify which store to download from (must match upload store name) -->
    <!-- Note: 'store' is only required when using 'relative_path' to locate files.
         For 'base64_content' or 'http' sources, the store configuration is optional
         and can be omitted entirely. -->
    <store>primary</store>
    <!-- Note: Unlike upload which uses 'stores' (plural) for multiple destinations,
         download uses 'store' (singular) as files are retrieved from one location -->
  </file_management>
  
  <query>
  <![CDATA[
    USE [test]
    
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    
    -- Check if file exists
    IF NOT EXISTS (SELECT 1 FROM [files] WHERE id = @id)
    BEGIN
        DECLARE @err NVARCHAR(200) = 'No document found with id ' + CONVERT(NVARCHAR(50), @id);
        THROW 50404, @err, 1; -- Returns HTTP 404 Not Found
        RETURN;
    END
    
    -- Return file metadata
    -- The system needs at least one of: base64_content, relative_path, or http
    SELECT 
        file_name,              -- Name for the downloaded file
        relative_path,          -- Path in the file store
        -- base64_content,      -- Uncomment if file content is stored in DB
        -- 'application/pdf' AS content_type  -- Optional: specify MIME type
        -- 'https://example.com/file.pdf' AS http  -- Optional: proxy from URL
    FROM [files]
    WHERE id = @id;
  ]]>
  </query>
</download_document>
```

### SQL Query Response Fields

Your query should return a single row with these fields:

| Field | Required | Description |
|-------|----------|-------------|
| `file_name` | Recommended | Filename for download. Falls back to filename from `relative_path` or "downloaded_file" |
| `base64_content` | Conditional* | Base64-encoded file content (for database-stored files) |
| `relative_path` | Conditional* | Path to file in the configured store |
| `http` | Conditional* | HTTP/HTTPS URL to proxy the file from |
| `content_type` | Optional | MIME type (e.g., `application/pdf`). Auto-detected from extension if omitted |

*At least one content source (`base64_content`, `relative_path`, or `http`) must be provided.

### Usage Examples

#### Basic Download Request

```bash
GET https://localhost:7054/documents/b1c2d3e4-5678-90ab-cdef-1234567890ab
```

**Response Headers:**
```
Content-Type: application/pdf
Content-Disposition: attachment; filename="drivers_license.pdf"
```

**Response:** Binary file content (streamed)

#### Using cURL

```bash
# Download and save to file
curl -o document.pdf "https://localhost:7054/documents/b1c2d3e4-5678-90ab-cdef-1234567890ab"

# Download with custom filename from server
curl -OJ "https://localhost:7054/documents/b1c2d3e4-5678-90ab-cdef-1234567890ab"
```

#### Using Postman

1. Set request method to `GET`
2. Set URL to `https://localhost:7054/documents/{file-id}`
3. Click **Send and Download** button
4. Choose save location

### Download Source Examples

#### 1. Download from Local File Store

Most common scenario - files stored on local disk:

```xml
<file_management>
  <store>primary</store>
</file_management>

<!-- SQL Query returns: -->
SELECT 
    'invoice_2025.pdf' AS file_name,
    '2025/Nov/11/a3d5e7f9-1234-5678-90ab-cdef12345678/invoice_2025.pdf' AS relative_path
FROM [files] WHERE id = @id;
```

The system combines the store's `base_path` (from `file_management.xml`) with `relative_path` to locate the file.

#### 2. Download from SFTP Server

For files stored on remote SFTP servers:

```xml
<file_management>
  <store>remote_storage</store>
</file_management>

<!-- SQL Query returns the same relative_path -->
SELECT 
    'contract.docx' AS file_name,
    '2025/Nov/11/b2c3d4e5-6789-01bc-def1-234567890abc/contract.docx' AS relative_path
FROM [files] WHERE id = @id;
```

The system automatically:
- Connects to the SFTP server using credentials from `file_management.xml`
- Locates the file by combining both `base_path` and `relative_path`
- Opens a stream to the SFTP file
- Streams it directly to the client with minimal memory usage (only an 81KB buffer)

#### 3. Download from Database (Base64)

For files stored directly in the database (no file store needed):

```xml
<response_structure>file</response_structure>

<file_management>
  <!-- Store configuration can be omitted entirely for base64 content -->
</file_management>

<!-- SQL Query returns base64 content -->
SELECT 
    'small_image.png' AS file_name,
    'image/png' AS content_type,
    base64_content  -- Column containing base64-encoded file
FROM [files] WHERE id = @id;
```

> **Note**: Only suitable for small files (< 1MB). Large files should use file stores for better performance.

#### 4. Proxy Download from HTTP/HTTPS URL

Forward downloads from external URLs (useful for CDNs or external storage):

```xml
<response_structure>file</response_structure>

<file_management>
  <!-- Store configuration can be omitted entirely for HTTP proxy -->
</file_management>

<!-- SQL Query returns HTTP URL -->
SELECT 
    'external_document.pdf' AS file_name,
    'https://cdn.example.com/documents/file-12345.pdf' AS http
FROM [files] WHERE id = @id;
```

The system:
- Fetches the file from the URL
- Streams it to the client
- Preserves the content type from the HTTP response

### Advanced Download Scenarios

#### Download with Access Control

Combine with local API keys for secure downloads:

```xml
<download_protected_document>
  <route>secure/documents/{{id}}</route>
  <verb>GET</verb>
  <response_structure>file</response_structure>
  <mandatory_parameters>id</mandatory_parameters>
  
  <!-- Require API key authentication -->
  <local_api_keys>
    <key>secret-document-key-12345</key>
  </local_api_keys>
  
  <file_management>
    <store>primary</store>
  </file_management>
  
  <query>
  <![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @user_id UNIQUEIDENTIFIER = {{user_id}}; -- From authenticated context
    
    -- Verify user has access to this document
    IF NOT EXISTS (
        SELECT 1 FROM [files] f
        INNER JOIN [contacts] c ON f.contact_id = c.id
        WHERE f.id = @id AND c.user_id = @user_id
    )
    BEGIN
        THROW 50403, 'Access denied to this document', 1; -- HTTP 403 Forbidden
        RETURN;
    END
    
    SELECT file_name, relative_path
    FROM [files] WHERE id = @id;
  ]]>
  </query>
</download_protected_document>
```

#### Download with Audit Logging

Track who downloads what:

```sql
DECLARE @id UNIQUEIDENTIFIER = {{id}};
DECLARE @downloaded_by NVARCHAR(100) = {{user_email}};

-- Log the download
INSERT INTO [download_audit] (file_id, downloaded_by, downloaded_at)
VALUES (@id, @downloaded_by, GETDATE());

-- Return file metadata
SELECT file_name, relative_path
FROM [files] WHERE id = @id;
```

#### Dynamic Content Type Based on Client

Serve different file formats based on client preferences:

```sql
DECLARE @id UNIQUEIDENTIFIER = {{id}};
DECLARE @accept_header NVARCHAR(500) = {{Accept}}; -- HTTP Accept header

SELECT 
    CASE 
        WHEN @accept_header LIKE '%image/webp%' THEN 'image.webp'
        WHEN @accept_header LIKE '%image/png%' THEN 'image.png'
        ELSE 'image.jpg'
    END AS file_name,
    CASE 
        WHEN @accept_header LIKE '%image/webp%' THEN webp_path
        WHEN @accept_header LIKE '%image/png%' THEN png_path
        ELSE jpg_path
    END AS relative_path
FROM [file_variants] WHERE id = @id;
```

### Error Handling

The download feature handles common errors gracefully:

| Error | HTTP Status | When It Occurs |
|-------|-------------|----------------|
| File not found in database | 404 | SQL query returns no rows or throws 50404 |
| File not found in store | 404 | `relative_path` doesn't exist in the file store |
| Access denied | 403 | SQL query throws 50403 |
| Invalid store configuration | 500 | Store referenced but not configured in `file_management.xml` |
| SFTP connection failure | 500 | Cannot connect to SFTP server |
| HTTP proxy error | 502 | External URL returns error or is unreachable |

### Performance Considerations

**Streaming Architecture**: All download methods use async streaming:
- Files are never fully loaded into memory
- 81KB buffer size for optimal performance
- Supports files of any size efficiently

**SFTP Connection Pooling**: Multiple downloads from the same SFTP server reuse connections when possible.

**Local File Caching**: Consider using a CDN or reverse proxy cache for frequently downloaded files.

**HTTP Proxy Mode**: Best for files already hosted on high-performance CDNs. The API acts as a secure gateway.

> **Production Tip**: For high-traffic scenarios with large files, consider implementing:
> - Signed URLs with expiration (generate temporary direct download links)
> - CDN integration for static files
> - Range request support for resumable downloads (partially supported by ASP.NET Core automatically)

> **Security Note**: Always validate user permissions in your SQL query before returning file metadata. Never expose internal file paths to clients. Use GUIDs for file IDs rather than sequential integers.


## CORS (Cross-Origin Resource Sharing) Support

The solution provides flexible and powerful CORS support with pattern matching, allowing you to control which origins can access your API. CORS configuration can be set globally or per-endpoint with a sophisticated fallback system.

### How CORS Works in DBToRestAPI

When a browser makes a request from a different origin:
1. The browser sends an `Origin` header with the request
2. The system checks for CORS configuration (endpoint-specific → global → defaults)
3. If a regex pattern is defined, it matches the origin domain against the pattern
4. If matched, the origin is allowed; otherwise, the fallback origin is used
5. For requests without an `Origin` header (non-browser clients), the fallback origin is used
6. CORS headers are automatically added to the response

### Configuration Hierarchy

CORS settings follow this priority order:
1. **Endpoint-specific** - Defined in the endpoint's `<cors>` section in `sql.xml`
2. **Global** - Defined in `settings.xml` under `<cors>`
3. **Default** - Allows all origins (`*`) if nothing is configured

### Example - Basic CORS Configuration

Let's create an endpoint with CORS enabled:

**Configuration in `/config/sql.xml`:**

```xml
<api_with_cors>
  <route>api/public/data</route>
  <verb>GET</verb>
  
  <cors>
    <!-- Regex pattern to match allowed origins -->
    <!-- This pattern allows localhost and any subdomain of example.com -->
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    
    <!-- Fallback origin for non-matching origins or non-browser requests -->
    <fallback_origin><![CDATA[www.example.com]]></fallback_origin>
    
    <!-- Optional: Maximum age for preflight cache (default: 86400 = 1 day) -->
    <max_age>3600</max_age>
    
    <!-- Optional: Allow credentials (default: false unless 'authorize' is configured) -->
    <allow_credentials>true</allow_credentials>
    
    <!-- Optional: Specific headers to allow (default: * or standard headers if credentials enabled) -->
    <allowed_headers>Content-Type, Authorization, X-Api-Key</allowed_headers>
  </cors>
  
  <query>
  <![CDATA[
    SELECT 
      'Public data accessible from allowed origins' AS message,
      GETDATE() AS timestamp;
  ]]>
  </query>
</api_with_cors>
```

### CORS Pattern Matching

The `pattern` setting uses regular expressions to match origin domains:

**Example Patterns:**

```xml
<!-- Allow only localhost -->
<pattern><![CDATA[^localhost$]]></pattern>

<!-- Allow localhost with any port -->
<pattern><![CDATA[^localhost(:\d+)?$]]></pattern>

<!-- Allow any subdomain of example.com -->
<pattern><![CDATA[^.*\.example\.com$]]></pattern>

<!-- Allow multiple specific domains -->
<pattern><![CDATA[^(example\.com|another-domain\.com)$]]></pattern>

<!-- Allow localhost OR any subdomain of example.com -->
<pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>

<!-- Allow any subdomain of multiple domains -->
<pattern><![CDATA[^.*\.(example\.com|myapp\.io|partner\.net)$]]></pattern>
```

### CORS Behavior Examples

#### Scenario 1: Origin Matches Pattern

**Request from `https://app.example.com`:**
```http
GET /api/public/data
Origin: https://app.example.com
```

**Response Headers:**
```http
Access-Control-Allow-Origin: https://app.example.com
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization, X-Api-Key
Access-Control-Allow-Credentials: true
Access-Control-Max-Age: 3600
```

#### Scenario 2: Origin Doesn't Match Pattern

**Request from `https://unauthorized-site.com`:**
```http
GET /api/public/data
Origin: https://unauthorized-site.com
```

**Response Headers:**
```http
Access-Control-Allow-Origin: https://www.example.com
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization, X-Api-Key
Access-Control-Allow-Credentials: true
Access-Control-Max-Age: 3600
```

The browser will block the response since the origin doesn't match.

#### Scenario 3: Non-Browser Request (No Origin Header)

**Request from Postman/cURL:**
```http
GET /api/public/data
```

**Response Headers:**
```http
Access-Control-Allow-Origin: https://www.example.com
Access-Control-Allow-Methods: GET, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization, X-Api-Key
Access-Control-Allow-Credentials: true
Access-Control-Max-Age: 3600
```

### Global CORS Configuration

Define default CORS settings in `/config/settings.xml` that apply to all endpoints unless overridden:

```xml
<settings>
  <cors>
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    <fallback_origin><![CDATA[www.example.com]]></fallback_origin>
    <max_age>86400</max_age>
  </cors>
</settings>
```

**Endpoints without CORS configuration will inherit these global settings.**

### CORS Configuration Options

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `pattern` | No | None | Regex pattern to match allowed origin domains |
| `fallback_origin` | No | `*` | Origin used when pattern doesn't match or no Origin header |
| `max_age` | No | `86400` | Preflight cache duration in seconds |
| `allow_credentials` | No | `false`* | Whether to allow credentials (cookies, auth headers) |
| `allowed_headers` | No | `*`** | Comma-separated list of allowed headers |

*Automatically set to `true` if an `authorize` section exists (will be covered in authentication examples)

**Defaults to `*` when `allow_credentials` is false, or to a standard list when true: `Authorization, Content-Type, X-Requested-With, Accept, Origin, X-Api-Key`

### Allowed Methods

The `Access-Control-Allow-Methods` header is **automatically determined** from the endpoint's `verb` configuration:

```xml
<api_endpoint>
  <verb>GET, POST</verb>
  <!-- CORS will automatically allow: GET, POST, OPTIONS -->
</api_endpoint>
```

If no `verb` is specified, defaults to: `GET, POST, PUT, DELETE, PATCH, OPTIONS`

### Preflight Requests

CORS automatically handles preflight `OPTIONS` requests:

**Preflight Request:**
```http
OPTIONS /api/public/data
Origin: https://app.example.com
Access-Control-Request-Method: POST
Access-Control-Request-Headers: Content-Type, X-Api-Key
```

**Preflight Response:**
```http
HTTP/1.1 204 No Content
Access-Control-Allow-Origin: https://app.example.com
Access-Control-Allow-Methods: GET, POST, OPTIONS
Access-Control-Allow-Headers: Content-Type, X-Api-Key
Access-Control-Max-Age: 3600
```

The browser caches this for the duration specified in `max_age`.

### Real-World CORS Scenarios

#### Scenario 1: Public API for Any Origin

Allow all origins without restrictions:

```xml
<public_api>
  <route>api/public/weather</route>
  <cors>
    <!-- Omit pattern to allow any origin -->
    <fallback_origin>*</fallback_origin>
  </cors>
  <query>SELECT 'Public weather data' AS data;</query>
</public_api>
```

Or simply omit the `<cors>` section entirely - defaults to `*`.

#### Scenario 2: Multiple Development Environments

Allow localhost and development/staging subdomains:

```xml
<cors>
  <!-- Allow localhost, dev.example.com, staging.example.com, etc. -->
  <pattern><![CDATA[^(localhost|dev\.example\.com|staging\.example\.com)$]]></pattern>
  <fallback_origin>https://example.com</fallback_origin>
</cors>
```

#### Scenario 3: Production with Specific Subdomains

Allow only production domains:

```xml
<cors>
  <!-- Allow www.example.com and app.example.com -->
  <pattern><![CDATA[^(www|app)\.example\.com$]]></pattern>
  <fallback_origin>https://www.example.com</fallback_origin>
  <allow_credentials>true</allow_credentials>
</cors>
```

#### Scenario 4: Multi-Tenant Application

Allow any subdomain for a multi-tenant SaaS:

```xml
<cors>
  <!-- Allow any subdomain like tenant1.myapp.com, tenant2.myapp.com, etc. -->
  <pattern><![CDATA[^.*\.myapp\.com$]]></pattern>
  <fallback_origin>https://www.myapp.com</fallback_origin>
</cors>
```

#### Scenario 5: Partner API Integration

Allow specific partner domains:

```xml
<cors>
  <!-- Allow requests from multiple partner domains -->
  <pattern><![CDATA[^(partner1\.com|partner2\.net|client\.io)$]]></pattern>
  <fallback_origin>https://partners.myapi.com</fallback_origin>
  <allowed_headers>Content-Type, Authorization, X-Partner-Key</allowed_headers>
</cors>
```

### Testing CORS Configuration

#### Using cURL

```bash
# Test with Origin header (simulating browser request)
curl -i -H "Origin: https://app.example.com" \
  https://localhost:7054/api/public/data

# Test preflight OPTIONS request
curl -i -X OPTIONS \
  -H "Origin: https://app.example.com" \
  -H "Access-Control-Request-Method: POST" \
  -H "Access-Control-Request-Headers: Content-Type" \
  https://localhost:7054/api/public/data
```

#### Using JavaScript (Browser)

```javascript
// This will trigger CORS validation in the browser
fetch('https://localhost:7054/api/public/data', {
  method: 'GET',
  headers: {
    'Content-Type': 'application/json',
    'X-Api-Key': 'your-key-here'
  },
  credentials: 'include' // Send cookies if allow_credentials is true
})
.then(response => response.json())
.then(data => console.log(data))
.catch(error => console.error('CORS Error:', error));
```

### CORS Configuration Per Endpoint vs Global

You can mix and match CORS configurations:

```xml
<!-- settings.xml - Global default -->
<cors>
  <pattern><![CDATA[^localhost$]]></pattern>
  <fallback_origin>*</fallback_origin>
</cors>

<!-- sql.xml - Endpoint-specific override -->
<strict_api>
  <route>api/sensitive/data</route>
  <cors>
    <!-- This endpoint has stricter CORS rules -->
    <pattern><![CDATA[^app\.example\.com$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
    <allow_credentials>true</allow_credentials>
  </cors>
  <query>SELECT 'Sensitive data' AS data;</query>
</strict_api>

<public_api>
  <route>api/public/data</route>
  <!-- No CORS config - inherits global settings -->
  <query>SELECT 'Public data' AS data;</query>
</public_api>
```

### Troubleshooting CORS Issues

**Issue**: CORS error in browser console

**Solutions:**
1. Check that the `Origin` header matches your pattern
2. Verify `allow_credentials` is `true` if sending cookies/auth headers
3. Ensure `allowed_headers` includes all headers you're sending
4. Check browser console for specific CORS error messages

**Issue**: Preflight OPTIONS returns 404

**Solution:** The CORS middleware handles OPTIONS automatically. Ensure your route configuration is correct.

**Issue**: Works in Postman but not in browser

**Explanation:** Postman doesn't enforce CORS. Browsers do. Test with actual Origin headers in Postman or use browser testing.

### Security Best Practices

1. **Use Specific Patterns**: Avoid overly broad patterns like `.*` in production
2. **Validate Fallback Origins**: Don't use `*` for APIs requiring authentication
3. **Limit Credentials**: Only set `allow_credentials: true` when necessary
4. **Restrict Headers**: Specify only required headers instead of `*`
5. **Monitor Origins**: Log blocked origins to detect potential security issues
6. **Environment-Specific**: Use different patterns for dev/staging/production

> **Security Note**: When `allow_credentials` is `true`, you cannot use `*` for `Access-Control-Allow-Origin`. The system automatically uses either the matched origin or the fallback origin to comply with CORS security requirements.

> **Performance Tip**: Set a reasonable `max_age` (default: 1 day) to reduce preflight requests. Browsers cache the preflight response for this duration, improving performance for repeated requests.


## OIDC/JWT Authorization

The solution provides enterprise-grade JWT (JSON Web Token) authentication with support for multiple OIDC (OpenID Connect) providers including Azure B2C, Azure AD, Google, Facebook, Auth0, Okta, and any OIDC-compliant identity provider.

### Key Features

- ✅ **Multi-Provider Support** - Configure multiple identity providers (Azure B2C, Google, Auth0, etc.)
- ✅ **Automatic Token Validation** - Validates JWT signatures, issuer, audience, and expiration
- ✅ **UserInfo Fallback** - Automatically fetches missing claims from OIDC UserInfo endpoint
- ✅ **Claims in SQL** - Access user claims directly in SQL queries (e.g., `{auth{email}}`, `{auth{sub}}`)
- ✅ **Role & Scope Enforcement** - Require specific roles or scopes for endpoints
- ✅ **Smart Caching** - Caches OIDC discovery documents and UserInfo responses
- ✅ **Flexible Configuration** - Global, provider-level, and endpoint-level settings
- ✅ **Automatic CORS Integration** - Sets `Access-Control-Allow-Credentials: true` when auth is enabled

### How JWT Authorization Works

1. **Client Authentication**: User signs in via your identity provider (Azure B2C, Google, etc.)
2. **Token Acquisition**: Client receives a JWT access token or ID token
3. **API Request**: Client sends token in `Authorization: Bearer {token}` header
4. **Token Validation**: 
   - System fetches OIDC discovery document (cached)
   - Validates token signature using provider's public keys
   - Verifies issuer, audience, and expiration
5. **Claims Extraction**: User claims (email, name, roles, etc.) are extracted
6. **UserInfo Fallback** (if configured): Missing claims are fetched from UserInfo endpoint (cached)
7. **SQL Access**: Claims become available in SQL as `{auth{claim_name}}` parameters
8. **Authorization**: Your SQL query can check user roles, permissions, etc.

### Prerequisites - Provider Configuration

Configure your identity providers in `/config/auth_providers.xml`:

```xml
<settings>
  <authorize>
    <providers>
      
      <!-- Azure AD B2C Configuration -->
      <azure_b2c>
        <!-- OIDC authority URL (discovery document at /.well-known/openid-configuration) -->
        <authority>https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin</authority>
        
        <!-- Expected audience (your API's client ID in Azure B2C) -->
        <audience>your-api-client-id</audience>
        
        <!-- Optional: Issuer (must match 'iss' claim exactly) -->
        <issuer>https://yourb2c.b2clogin.com/tenant-id/v2.0/</issuer>
        
        <!-- Token Validation Settings -->
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
        <validate_lifetime>true</validate_lifetime>
        <clock_skew_seconds>300</clock_skew_seconds>
        
        <!-- UserInfo Fallback: fetch these claims if missing from token -->
        <userinfo_fallback_claims>email,name,given_name,family_name</userinfo_fallback_claims>
        <userinfo_cache_duration_seconds>300</userinfo_cache_duration_seconds>
      </azure_b2c>
      
      <!-- Google OIDC Configuration -->
      <google>
        <authority>https://accounts.google.com</authority>
        <audience>your-google-client-id.apps.googleusercontent.com</audience>
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
        <validate_lifetime>true</validate_lifetime>
        <userinfo_fallback_claims>email,name,picture</userinfo_fallback_claims>
      </google>
      
      <!-- Auth0 Configuration -->
      <auth0>
        <authority>https://your-domain.auth0.com/</authority>
        <audience>https://your-api-identifier</audience>
        <validate_issuer>true</validate_issuer>
        <validate_audience>true</validate_audience>
      </auth0>
      
    </providers>
  </authorize>
</settings>
```

### Example - Protected Endpoint with JWT

Let's create an endpoint that requires authentication and uses claims in the SQL query:

**Configuration in `/config/sql.xml`:**

```xml
<hello_world_auth>
  <route>auth/hello_world</route>
  <verb>GET</verb>
  
  <!-- Enable JWT authorization using the 'azure_b2c' provider -->
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  
  <!-- CORS automatically allows credentials when 'authorize' is present -->
  <cors>
    <pattern><![CDATA[^(localhost|.*\.example\.com)$]]></pattern>
    <fallback_origin>https://www.example.com</fallback_origin>
  </cors>
  
  <query>
  <![CDATA[
    -- Access JWT claims using {auth{claim_name}} syntax
    DECLARE @user_name NVARCHAR(500) = {auth{name}};
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    DECLARE @user_id NVARCHAR(100) = {auth{sub}};
    
    -- You can also access claims with dots using underscores
    -- e.g., if claim is 'user.email', use {auth{user_email}}
    
    -- Example: Look up user in your database
    DECLARE @is_active BIT;
    SELECT @is_active = active FROM users WHERE email = @user_email;
    
    -- Authorization: Check if user is active
    IF @is_active IS NULL OR @is_active = 0
    BEGIN
        THROW 50403, 'User account is inactive', 1; -- Returns HTTP 403 Forbidden
        RETURN;
    END
    
    IF @user_name IS NULL OR LTRIM(RTRIM(@user_name)) = ''
    BEGIN
        SET @user_name = 'authenticated user';
    END
    
    SELECT 
        'Hello ' + @user_name + '! Your email is ' + @user_email AS message,
        @user_id AS user_id,
        GETDATE() AS timestamp;
  ]]>
  </query>
</hello_world_auth>
```

### Usage - Making Authenticated Requests

#### Using JavaScript (React/Angular/Vue)

```javascript
// After user signs in, you'll have an access token
const accessToken = 'eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...';

fetch('https://localhost:7054/auth/hello_world', {
  method: 'GET',
  headers: {
    'Authorization': `Bearer ${accessToken}`,
    'Content-Type': 'application/json'
  },
  credentials: 'include' // Important for CORS with credentials
})
.then(response => response.json())
.then(data => console.log(data))
.catch(error => console.error('Auth Error:', error));
```

#### Using cURL

```bash
# Replace TOKEN with your actual JWT
curl -H "Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9..." \
  https://localhost:7054/auth/hello_world
```

#### Using Postman

1. Select **Authorization** tab
2. Choose **Type**: Bearer Token
3. Paste your JWT in the **Token** field
4. Send the request

**Successful Response:**
```json
{
  "message": "Hello John Doe! Your email is john@example.com",
  "user_id": "a3d5e7f9-1234-5678-90ab-cdef12345678",
  "timestamp": "2025-11-21T10:30:45.123"
}
```

**Error Response (Missing/Invalid Token):**
```json
{
  "success": false,
  "message": "Authorization header is required"
}
```
HTTP Status: `401 Unauthorized`

### Available JWT Claims in SQL

All claims from the JWT are accessible using the `{auth{claim_name}}` syntax. Common claims include:

| Claim | Example Usage | Description |
|-------|---------------|-------------|
| `sub` | `{auth{sub}}` | Subject - unique user identifier |
| `email` | `{auth{email}}` | User's email address |
| `name` | `{auth{name}}` | User's full name |
| `given_name` | `{auth{given_name}}` | First name |
| `family_name` | `{auth{family_name}}` | Last name |
| `oid` | `{auth{oid}}` | Object ID (Azure AD) |
| `roles` | `{auth{roles}}` | User roles (pipe-delimited if multiple) |
| `scp` / `scope` | `{auth{scope}}` | Scopes/permissions |
| `tfp` | `{auth{tfp}}` | Trust Framework Policy (Azure B2C user flow) |
| `idp` | `{auth{idp}}` | Identity Provider (Google, Facebook, etc.) |
| `extension_*` | `{auth{extension_SubscriptionLevel}}` | Custom attributes |

**Handling Claims with Special Characters:**
- Claim: `user.email` → Use: `{auth{user_email}}`
- Claim: `https://schemas.example.com/role` → Use: `{auth{https___schemas_example_com_role}}`

All dots, slashes, and special characters are replaced with underscores.

### Authorization Configuration Options

#### Endpoint-Level Configuration

Override provider settings for specific endpoints:

```xml
<sensitive_endpoint>
  <route>api/admin/users</route>
  
  <authorize>
    <!-- Use a different provider for this endpoint -->
    <provider>azure_ad</provider>
    
    <!-- Override token validation settings -->
    <validate_issuer>true</validate_issuer>
    <validate_audience>true</validate_audience>
    <validate_lifetime>true</validate_lifetime>
    <clock_skew_seconds>60</clock_skew_seconds>
    
    <!-- Require specific scopes (comma-separated) -->
    <required_scopes>api.read, api.write</required_scopes>
    
    <!-- Require specific roles (comma-separated) -->
    <required_roles>admin, superuser</required_roles>
    
    <!-- UserInfo fallback settings -->
    <userinfo_fallback_claims>email,name</userinfo_fallback_claims>
    <userinfo_cache_duration_seconds>600</userinfo_cache_duration_seconds>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    -- Admin operations here
    SELECT * FROM sensitive_data WHERE owner = @user_email;
  ]]>
  </query>
</sensitive_endpoint>
```

#### Disable Authorization for Specific Endpoint

You can keep the provider configured but disable authorization for a specific endpoint when testing.
Not having `<authorize>` section also disables authorization. This might come handy during development where you want to temporarily toggle on/off authorization without removing the `<provider>` config tag. Default is `enabled=true`, so if it's not defined, authorization is enabled.


> Note: NOT having `<authorize>` section at all will be just as having it with `<enabled>false</enabled>`
```xml
<public_endpoint>
  <route>api/public/data</route>
  
  <!-- Explicitly disable authorization -->
  <authorize>
    <provider>azure_ad</provider>
    <enabled>false</enabled>
  </authorize>
  
  <query>SELECT 'Public data' AS data;</query>
</public_endpoint>
```

#### Provider-Level Configuration

Define settings in `auth_providers.xml` that apply to all endpoints using that provider:

```xml
<azure_b2c>
  <authority>https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin</authority>
  <audience>your-api-client-id</audience>
  
  <!-- Global scope/role requirements for this provider -->
  <required_scopes>api.read</required_scopes>
  <required_roles>user</required_roles>
  
  <!-- These settings apply to all endpoints using azure_b2c -->
  <validate_issuer>true</validate_issuer>
  <validate_lifetime>true</validate_lifetime>
  <clock_skew_seconds>300</clock_skew_seconds>
</azure_b2c>
```

### Configuration Hierarchy

Settings follow this priority order:
1. **Endpoint-specific** - `<authorize>` section in `sql.xml`
2. **Provider-level** - Provider configuration in `auth_providers.xml`

### Advanced Authorization Scenarios

#### Scenario 1: Database-Driven Authorization (Most Common Pattern)

**Use Case:** Social logins (Google, Facebook, Microsoft) or any OIDC provider that only returns basic identity claims (email, name, sub) without roles. Your application stores user roles and permissions in your own database.

**Why This Pattern:**
- ✅ OIDC providers (Google, Facebook, etc.) don't know your app's business logic
- ✅ ID tokens provide identity proof, not authorization
- ✅ Roles/permissions managed in your database (source of truth)
- ✅ Instant role changes (no sign-out / sign-in needed to force token refresh)
- ✅ Supports complex authorization logic in SQL

**Database Schema:**

```sql
-- User table with roles managed in your database
CREATE TABLE [dbo].[app_users] (
    [id] UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    [email] NVARCHAR(500) NOT NULL UNIQUE,
    [name] NVARCHAR(500) NULL,
    [role] NVARCHAR(100) NOT NULL, -- 'admin', 'user', 'manager', etc.
    [subscription_tier] NVARCHAR(50) NULL, -- 'free', 'pro', 'enterprise'
    [is_active] BIT NOT NULL DEFAULT 1,
    [created_at] DATETIME2 NOT NULL DEFAULT GETDATE(),
    [last_login] DATETIME2 NULL
);

CREATE INDEX IX_app_users_email ON [dbo].[app_users]([email]);
```

**Endpoint Configuration:**

```xml
<get_user_data>
  <route>api/user/data</route>
  <verb>GET</verb>
  
  <authorize>
    <!-- Google only provides email, name, picture in ID token -->
    <provider>google</provider>
  </authorize>
  
  <query>
  <![CDATA[
    -- JWT only provides identity (email), lookup authorization in database
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    DECLARE @user_name NVARCHAR(500) = {auth{name}};
    
    -- Lookup user in database to get roles and permissions
    DECLARE @user_id UNIQUEIDENTIFIER;
    DECLARE @user_role NVARCHAR(100);
    DECLARE @is_active BIT;
    DECLARE @subscription_tier NVARCHAR(50);
    
    SELECT 
        @user_id = id,
        @user_role = role,
        @is_active = is_active,
        @subscription_tier = subscription_tier
    FROM app_users 
    WHERE email = @user_email;
    
    -- If user doesn't exist in database, create them (first-time login)
    IF @user_id IS NULL
    BEGIN
        INSERT INTO app_users (email, name, role, is_active)
        VALUES (@user_email, @user_name, 'user', 1); -- Default role: 'user'
        
        SET @user_id = SCOPE_IDENTITY();
        SET @user_role = 'user';
        SET @is_active = 1;
        SET @subscription_tier = 'free';
    END
    ELSE
    BEGIN
        -- Update last login timestamp
        UPDATE app_users 
        SET last_login = GETDATE()
        WHERE id = @user_id;
    END
    
    -- Authorization: Check if user is active
    IF @is_active = 0
    BEGIN
        THROW 50403, 'Your account has been deactivated. Please contact support.', 1;
        RETURN;
    END
    
    -- Authorization: Check subscription tier for premium features
    IF @subscription_tier = 'free'
    BEGIN
        -- Return limited data for free users
        SELECT 
            'Welcome ' + @user_name AS message,
            @user_role AS role,
            'Upgrade to Pro for more features!' AS upgrade_prompt,
            (SELECT TOP 10 * FROM user_data WHERE user_id = @user_id FOR JSON PATH) AS data;
    END
    ELSE
    BEGIN
        -- Return full data for paid users
        SELECT 
            'Welcome ' + @user_name AS message,
            @user_role AS role,
            @subscription_tier AS subscription,
            (SELECT * FROM user_data WHERE user_id = @user_id FOR JSON PATH) AS data;
    END
  ]]>
  </query>
</get_user_data>
```

**Admin-Only Endpoint Example:**

```xml
<admin_dashboard>
  <route>api/admin/dashboard</route>
  <verb>GET</verb>
  
  <authorize>
    <provider>google</provider>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    
    -- Check if user has admin role in database
    DECLARE @user_role NVARCHAR(100);
    SELECT @user_role = role FROM app_users WHERE email = @user_email;
    
    IF @user_role IS NULL
    BEGIN
        THROW 50404, 'User not found in system', 1;
        RETURN;
    END
    
    IF @user_role NOT IN ('admin', 'superadmin')
    BEGIN
        THROW 50403, 'Admin access required', 1; -- HTTP 403 Forbidden
        RETURN;
    END
    
    -- Log admin access
    INSERT INTO audit_log (user_email, action, timestamp)
    VALUES (@user_email, 'accessed_admin_dashboard', GETDATE());
    
    -- Return admin dashboard data
    SELECT 
        (SELECT COUNT(*) FROM app_users) AS total_users,
        (SELECT COUNT(*) FROM app_users WHERE is_active = 1) AS active_users,
        (SELECT COUNT(*) FROM app_users WHERE role = 'admin') AS admin_count,
        (SELECT TOP 10 * FROM audit_log ORDER BY timestamp DESC FOR JSON PATH) AS recent_activity;
  ]]>
  </query>
</admin_dashboard>
```

**Client-Side (React with Google Sign-In):**

```javascript
import { GoogleOAuthProvider, useGoogleLogin } from '@react-oauth/google';

function App() {
  const login = useGoogleLogin({
    onSuccess: async (tokenResponse) => {
      // Google returns ID token, not access token for user info
      const idToken = tokenResponse.id_token;
      
      // Call your API with the ID token
      const response = await fetch('https://api.example.com/api/user/data', {
        headers: {
          'Authorization': `Bearer ${idToken}`,
          'Content-Type': 'application/json'
        },
        credentials: 'include'
      });
      
      const userData = await response.json();
      console.log('User role from database:', userData.role);
      console.log('Subscription tier:', userData.subscription);
    }
  });
  
  return <button onClick={() => login()}>Sign in with Google</button>;
}
```

**Key Advantages of This Pattern:**

1. **Instant Authorization Changes**: Update user roles in database, effective immediately (no token refresh)
2. **Complex Business Logic**: Multi-tenant, subscription tiers, feature flags - all in SQL
3. **Single Source of Truth**: Your database controls access, not OIDC provider
4. **Onboarding**: Auto-create users on first login with default role
5. **Audit Trail**: Track all authorization decisions in your database
6. **Provider Agnostic**: Works with any OIDC provider (Google, Facebook, Microsoft, Apple, etc.)

> **Note**: This pattern is recommended for most applications. The token proves identity (`{auth{email}}`), your database controls authorization (`SELECT role FROM app_users`).

#### Scenario 2: Role-Based Access Control (Roles in Token)

**Use Case:** When your OIDC provider (Azure B2C, Auth0, Okta) includes roles in the token claims.

```xml
<admin_users_endpoint>
  <route>api/admin/users</route>
  
  <authorize>
    <provider>azure_b2c</provider>
    <!-- Only users with 'admin' role can access -->
    <required_roles>admin</required_roles>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @user_email NVARCHAR(500) = {auth{email}};
    DECLARE @user_roles NVARCHAR(1000) = {auth{roles}};
    
    -- Additional role checks in SQL if needed
    IF @user_roles NOT LIKE '%superadmin%' AND @user_roles NOT LIKE '%admin%'
    BEGIN
        THROW 50403, 'Admin privileges required', 1;
        RETURN;
    END
    
    -- Log admin action
    INSERT INTO audit_log (user_email, action, timestamp)
    VALUES (@user_email, 'viewed_users', GETDATE());
    
    SELECT * FROM users;
  ]]>
  </query>
</admin_users_endpoint>
```

**Response when role check fails:**
```json
{
  "success": false,
  "message": "Insufficient permissions"
}
```
HTTP Status: `403 Forbidden`

#### Scenario 3: Scope-Based Access

```xml
<user_profile_endpoint>
  <route>api/profile</route>
  
  <authorize>
    <provider>azure_b2c</provider>
    <!-- Require both read and write scopes -->
    <required_scopes>profile.read, profile.write</required_scopes>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @user_id NVARCHAR(100) = {auth{sub}};
    SELECT * FROM user_profiles WHERE user_id = @user_id;
  ]]>
  </query>
</user_profile_endpoint>
```

#### Scenario 4: Row-Level Security with JWT Claims

```xml
<my_orders_endpoint>
  <route>api/orders</route>
  
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @user_id NVARCHAR(100) = {auth{sub}};
    DECLARE @tenant_id NVARCHAR(100) = {auth{extension_TenantId}};
    
    -- Users can only see their own orders within their tenant
    SELECT * FROM orders 
    WHERE user_id = @user_id AND tenant_id = @tenant_id
    ORDER BY order_date DESC;
  ]]>
  </query>
</my_orders_endpoint>
```

#### Scenario 5: Audit Logging with User Context

```xml
<update_contact_auth>
  <route>api/contacts/{{id}}</route>
  <verb>PUT</verb>
  
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @id UNIQUEIDENTIFIER = {{id}};
    DECLARE @name NVARCHAR(500) = {{name}};
    DECLARE @phone NVARCHAR(100) = {{phone}};
    
    -- Get user info from JWT claims
    DECLARE @updated_by NVARCHAR(500) = {auth{email}};
    DECLARE @updated_by_name NVARCHAR(500) = {auth{name}};
    
    -- Update contact with audit trail
    UPDATE contacts 
    SET 
      name = @name,
      phone = @phone,
      updated_at = GETDATE(),
      updated_by = @updated_by,
      updated_by_name = @updated_by_name
    WHERE id = @id;
    
    -- Log the change
    INSERT INTO contact_audit (
      contact_id, action, performed_by, performed_at
    )
    VALUES (
      @id, 'update', @updated_by, GETDATE()
    );
    
    SELECT * FROM contacts WHERE id = @id;
  ]]>
  </query>
</update_contact_auth>
```

#### Scenario 6: Multi-Tenant SaaS with JWT Claims

```xml
<tenant_data_endpoint>
  <route>api/tenants/{{tenant_id}}/data</route>
  
  <authorize>
    <provider>azure_b2c</provider>
  </authorize>
  
  <query>
  <![CDATA[
    DECLARE @requested_tenant_id NVARCHAR(100) = {{tenant_id}};
    DECLARE @user_tenant_id NVARCHAR(100) = {auth{extension_TenantId}};
    DECLARE @user_role NVARCHAR(100) = {auth{roles}};
    
    -- Verify user belongs to the requested tenant
    IF @requested_tenant_id != @user_tenant_id
    BEGIN
        -- Unless they're a superadmin
        IF @user_role NOT LIKE '%superadmin%'
        BEGIN
            THROW 50403, 'Access denied to this tenant', 1;
            RETURN;
        END
    END
    
    SELECT * FROM tenant_data 
    WHERE tenant_id = @requested_tenant_id;
  ]]>
  </query>
</tenant_data_endpoint>
```

### UserInfo Endpoint Fallback

Some OIDC providers (Google, Facebook, social logins) issue access tokens that don't contain all user claims. The system automatically fetches missing claims from the provider's UserInfo endpoint.

**How it works:**
1. System checks if configured claims (e.g., `email`, `name`) are missing from the token
2. If missing, calls the UserInfo endpoint with the access token
3. Merges UserInfo claims with token claims
4. Caches the result based on token hash and expiration

**Configuration:**

```xml
<azure_b2c>
  <authority>https://yourb2c.b2clogin.com/...</authority>
  <audience>your-api-client-id</audience>
  
  <!-- Claims to fetch from UserInfo if missing from token -->
  <userinfo_fallback_claims>email,name,given_name,family_name</userinfo_fallback_claims>
  
  <!-- Cache UserInfo responses for 5 minutes (or token expiry, whichever is shorter) -->
  <userinfo_cache_duration_seconds>300</userinfo_cache_duration_seconds>
</azure_b2c>
```

**Smart Caching:**
- Cache duration never exceeds token expiration
- If `userinfo_cache_duration_seconds` = 300 and token expires in 120 seconds → cache for 120 seconds
- If `userinfo_cache_duration_seconds` = 300 and token expires in 3600 seconds → cache for 300 seconds
- If `userinfo_cache_duration_seconds` not configured → cache until token expiry

### Token Validation Details

The system performs comprehensive JWT validation:

1. **Signature Validation**: Verifies token was signed by the identity provider using JWKS (JSON Web Key Set)
2. **Issuer Validation**: Ensures `iss` claim matches configured issuer
3. **Audience Validation**: Ensures `aud` claim matches configured audience
4. **Expiration Validation**: Checks `exp` claim with configurable clock skew
5. **Not Before Validation**: Checks `nbf` claim if present

**Clock Skew:**
Accounts for time differences between servers (default: 5 minutes):

```xml
<clock_skew_seconds>300</clock_skew_seconds>
```

If token expires at 10:00:00 with 5-minute skew, it's accepted until 10:05:00.

### Error Responses

| Error | HTTP Status | When It Occurs |
|-------|-------------|----------------|
| Missing Authorization header | 401 | Request doesn't include `Authorization: Bearer {token}` |
| Invalid token format | 401 | Header doesn't start with "Bearer " |
| Empty token | 401 | Token is blank or whitespace only |
| Token validation failed | 401 | Invalid signature, expired, wrong issuer/audience |
| Missing required scopes | 403 | User doesn't have required scopes |
| Missing required roles | 403 | User doesn't have required roles |
| Provider not found | 500 | Referenced provider doesn't exist in config |
| Authority not configured | 500 | Provider missing `authority` setting |

### Real-World Integration Example - React with Azure B2C

**React Client Code:**

```jsx
import { PublicClientApplication } from '@azure/msal-browser';

const msalConfig = {
  auth: {
    clientId: 'your-client-id',
    authority: 'https://yourb2c.b2clogin.com/yourb2c.onmicrosoft.com/B2C_1_signupsignin',
    knownAuthorities: ['yourb2c.b2clogin.com']
  }
};

const msalInstance = new PublicClientApplication(msalConfig);

// Sign in
async function signIn() {
  const response = await msalInstance.loginPopup({
    scopes: ['openid', 'profile', 'email']
  });
  return response.accessToken;
}

// Call protected API
async function getHelloWorld() {
  const accounts = msalInstance.getAllAccounts();
  const response = await msalInstance.acquireTokenSilent({
    scopes: ['openid'],
    account: accounts[0]
  });
  
  const apiResponse = await fetch('https://localhost:7054/auth/hello_world', {
    method: 'GET',
    headers: {
      'Authorization': `Bearer ${response.accessToken}`,
      'Content-Type': 'application/json'
    },
    credentials: 'include'
  });
  
  return await apiResponse.json();
}
```

### Testing JWT Authentication

#### Using jwt.io

1. Copy your JWT token
2. Visit https://jwt.io
3. Paste token in the "Encoded" section
4. Verify claims in the "Decoded" section
5. Check signature is valid (green checkmark)

#### Using Postman

1. Get a token from your identity provider
2. In Postman, select **Authorization** tab
3. Type: **Bearer Token**
4. Paste token
5. Send request

#### Debugging Token Issues

Enable debug logging in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "DBToRestAPI.Middlewares.Step5JwtAuthorization": "Debug"
    }
  }
}
```

This logs:
- Token validation steps
- Claims extracted
- UserInfo endpoint calls
- Scope/role validation results

### Security Best Practices

1. **Use HTTPS in Production**: Never send JWTs over unencrypted HTTP
2. **Short Token Lifetimes**: Configure 1-hour or less token expiration
3. **Validate Audience**: Always configure correct `audience` to prevent token reuse
4. **Secure Secrets**: Never commit `auth_providers.xml` with real credentials to source control
5. **Environment-Specific Config**: Use different providers/settings for dev/staging/production
6. **Monitor Invalid Attempts**: Log failed authentication attempts
7. **Implement Token Refresh**: Use refresh tokens in your client application
8. **Scope Principle of Least Privilege**: Only grant necessary scopes/roles
9. **Regular Key Rotation**: Identity providers automatically rotate signing keys - system handles this via JWKS

### Performance Considerations

**OIDC Discovery Caching:**
- Discovery documents cached for 24 hours (or until server restart)
- Reduces latency by 100-200ms per request
- Automatically refreshes when cache expires

**UserInfo Caching:**
- Responses cached based on token hash
- Respects token expiration time
- Can save 50-100ms per request for social logins

**Token Validation:**
- Signature validation is CPU-intensive (~5-10ms)
- Cached JWKS reduces validation to ~1-2ms after first request
- Async processing doesn't block request pipeline

> **Security Note**: JWT authorization works seamlessly with CORS - when an `authorize` section exists, `Access-Control-Allow-Credentials: true` is automatically set. Ensure your `cors:pattern` is restrictive and doesn't allow untrusted origins.


## Settings Encryption

The solution provides automatic encryption of sensitive configuration values such as connection strings, API secrets, and passwords. Sensitive values are automatically encrypted on first startup and decrypted at runtime.

### Quick Start (Windows - DPAPI)

On Windows, encryption works automatically using DPAPI (Data Protection API) with no additional setup required.

1. Add the `settings_encryption` section to your `/config/settings.xml`:

```xml
<settings_encryption>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
    <section>authorize:providers:azure_b2c:client_secret</section>
  </sections_to_encrypt>
</settings_encryption>
```

2. Run the application. On first startup:
   - Unencrypted values are automatically encrypted and saved back to the XML files
   - Values are decrypted in memory for runtime use
   - Your config files now contain encrypted values like `encrypted:CfDJ8NhY2kB...`

**Before (unencrypted):**
```xml
<ConnectionStrings>
  <default>Server=myserver;Password=MySecret123!</default>
</ConnectionStrings>
```

**After first startup (automatically encrypted):**
```xml
<ConnectionStrings>
  <default>encrypted:CfDJ8NhY2kB...very-long-base64-string...</default>
</ConnectionStrings>
```

Your application code accesses values normally - decryption is transparent.

### Cross-Platform Encryption

For Linux, macOS, Docker, or Kubernetes deployments, configure the ASP.NET Core Data Protection API with a key directory:

```xml
<settings_encryption>
  <data_protection_key_path>./keys/</data_protection_key_path>
  <sections_to_encrypt>
    <section>ConnectionStrings</section>
  </sections_to_encrypt>
</settings_encryption>
```

Or use an environment variable:
```bash
DATA_PROTECTION_KEY_PATH=./keys/
```

> **Important**: Persist the keys directory! Losing keys means losing access to encrypted values.

### Encryption Method Priority

1. **If `data_protection_key_path` is configured** → ASP.NET Core Data Protection API (cross-platform)
2. **Else if running on Windows** → DPAPI (machine-bound)
3. **Else** → Encryption disabled (passthrough mode)

### What Can Be Encrypted

Specify configuration paths in `sections_to_encrypt`:

| Path | What Gets Encrypted |
|------|---------------------|
| `ConnectionStrings` | All connection strings |
| `ConnectionStrings:default` | Only the "default" connection string |
| `authorize:providers:azure_b2c` | All values under azure_b2c |
| `authorize:providers:azure_b2c:client_secret` | Only the client_secret |

> **📖 Full Documentation**: For complete details on cross-platform setup, Docker/Kubernetes deployment, key management, and migration between encryption methods, see [CONFIGURATION_MANAGEMENT.md](CONFIGURATION_MANAGEMENT.md#settings-encryption).


**documentation in progress - more examples to be added soon**