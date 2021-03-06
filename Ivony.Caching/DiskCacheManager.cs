﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ivony.Caching
{

  /// <summary>
  /// 磁盘缓存管理器，磁盘缓存管理器负责打开调度文件打开和关闭，过期缓存清理等
  /// </summary>
  internal sealed class DiskCacheManager : IDisposable
  {




    private bool _persistMode;


    /// <summary>
    /// 创建磁盘缓存管理器对象
    /// </summary>
    /// <param name="rootPath">缓存文件存放的路径</param>
    /// <param name="persistMode">持久模式，在此模式下，重启后将尽可能的使用原来的缓存目录</param>
    public DiskCacheManager( string rootPath, bool persistMode = true )
    {
      RootPath = rootPath;
      _persistMode = persistMode;
    }

    /// <summary>
    /// 分配一个新的缓存目录（一般用于清除缓存）
    /// </summary>
    internal void AssignCacheDirectory()
    {
      var directoryName = Path.GetRandomFileName();
      if ( _persistMode )
      {
        try
        {
          File.WriteAllText( Path.Combine( RootPath, "directory.cache" ), directoryName, Encoding.UTF8 );
        }
        catch ( IOException ) { }
      }

      CurrentDirectory = Path.Combine( RootPath, directoryName );
      Directory.CreateDirectory( CurrentDirectory );
    }



    internal void Initialize()
    {
      if ( _persistMode )
      {
        try
        {
          var directoryName = File.ReadAllText( Path.Combine( RootPath, "directory.cache" ), Encoding.UTF8 );
          var directory = Path.Combine( RootPath, directoryName );

          if ( Directory.Exists( directory ) )
            CurrentDirectory = directory;

        }
        catch ( IOException )
        {
        }
      }

      if ( CurrentDirectory == null )
        AssignCacheDirectory();

    }



    /// <summary>
    /// 缓存根目录
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// 当前目录
    /// </summary>
    public string CurrentDirectory { get; private set; }


    /// <summary>
    /// 读写缓冲区大小
    /// </summary>
    public int BufferSize { get; private set; } = 1024;


    private TaskManager tasks = new TaskManager();


    /// <summary>
    /// 读取一个流
    /// </summary>
    /// <param name="cacheKey">缓存键</param>
    /// <returns></returns>
    public async Task<Stream> ReadStream( string cacheKey )
    {
      var filepath = Path.Combine( CurrentDirectory, cacheKey );
      if ( File.Exists( filepath ) == false )
        return null;



      Task task = tasks.GetOrAdd( cacheKey, () => ReadStream( File.OpenRead( filepath ) ) );
      await task;

      var readTask = task as Task<byte[]>;
      if ( readTask != null )                //如果当前正在读，则以当前读取结果返回。
        return new MemoryStream( readTask.Result, false );

      else                                   //如果当前正在写，则再读取一次。
        return await ReadStream( cacheKey );

    }



    private async Task<byte[]> ReadStream( FileStream stream )
    {
      using ( stream )
      {
        var result = new MemoryStream();

        var buffer = new byte[BufferSize];

        while ( true )
        {
          var size = await stream.ReadAsync( buffer, 0, buffer.Length );
          result.Write( buffer, 0, size );


          if ( size < buffer.Length )
            break;
        }

        return result.ToArray();
      }
    }

    public Task WriteStream( string cacheKey, byte[] data )
    {
      return WriteStream( cacheKey, new MemoryStream( data ) );
    }


    public async Task WriteStream( string cacheKey, MemoryStream data )
    {
      var filepath = Path.Combine( CurrentDirectory, cacheKey );
      Directory.CreateDirectory( CurrentDirectory );



      bool added = false;


      var task = tasks.GetOrAdd( cacheKey, () =>
        {
          added = true;
          return WriteStream( File.OpenWrite( filepath ), data );
        } );

      await task;


      if ( added == false )     //如果任务未能加入队列，则再尝试一次
        await WriteStream( cacheKey, data );

    }


    /// <summary>
    /// 将数据写入文件流
    /// </summary>
    /// <param name="stream">文件流</param>
    /// <param name="data">数据</param>
    /// <returns></returns>
    private async Task WriteStream( FileStream stream, MemoryStream data )
    {
      data.Seek( 0, SeekOrigin.Begin );
      using ( stream )
      {
        await data.CopyToAsync( stream, BufferSize );
      }
    }

    public string ValidateCacheKey( string cacheKey )
    {
      if ( cacheKey.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 || cacheKey.Contains( '.' ) )
        return "cacheKey contains an invalid character";

      else
        return null;
    }

    public void Remove( string cacheKey )
    {
      var filepath = Path.Combine( CurrentDirectory, cacheKey + ".policy" );
      File.Delete( filepath );

    }




    /// <summary>
    /// 获取缓存策略对象
    /// </summary>
    /// <param name="cacheKey">缓存键</param>
    /// <returns>缓存策略对象</returns>
    public CachePolicyItem GetCachePolicy( string cacheKey )
    {
      var filepath = Path.Combine( CurrentDirectory, cacheKey + ".policy" );

      if ( File.Exists( filepath ) == false )
        return CachePolicyItem.InvalidCachePolicy;

      try
      {
        string data;
        using ( var reader = new StreamReader( new FileStream( filepath, FileMode.Open, FileAccess.Read, FileShare.Delete | FileShare.ReadWrite ), Encoding.UTF8 ) )
        {
          data = reader.ReadToEnd();
        }

        return CachePolicyItem.Parse( data );
      }
      catch ( IOException )
      {
        return CachePolicyItem.InvalidCachePolicy;
      }
    }



    /// <summary>
    /// 设置缓存策略对象
    /// </summary>
    /// <param name="cacheKey"></param>
    /// <param name="cachePolicy"></param>
    public void SetCachePolicy( string cacheKey, CachePolicyItem cachePolicy )
    {
      var filepath = Path.Combine( CurrentDirectory, cacheKey + ".policy" );

      using ( var writer = new StreamWriter( new FileStream( filepath, FileMode.Create, FileAccess.Write, FileShare.Delete ), Encoding.UTF8 ) )
      {
        writer.Write( cachePolicy.ToString() );
      }
    }

    /// <summary>
    /// 释放所有资源，删除文件夹
    /// </summary>
    public void Dispose()
    {

      Directory.Delete( RootPath, true );

    }
  }
}
