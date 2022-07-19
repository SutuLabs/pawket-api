#!/bin/bash

BASEPATH="$(dirname "$PWD")"

mkdir -p $BASEPATH/log
mkdir -p $BASEPATH/bin/syncer
mkdir -p $BASEPATH/bin/api
unzip -o NodeDBSyncer.zip -d $BASEPATH/bin/syncer
unzip -o WalletServer.zip -d $BASEPATH/bin/api

function start {
    NAME=$1
    WORKPATH=$2
    BINARY=$3
    PIDFILE=$BASEPATH/pid/${NAME}
    cd $WORKPATH
    source $BASEPATH/config/$NAME
    cat $BASEPATH/config/$NAME
    daemon --name=$NAME --running
    if [ $? -ne 0 ]; then
		echo "$NAME starting"
    else
		echo "$NAME restarting"
		pid=$(head -n 1 $PIDFILE)
		daemon --name=$NAME --stop
		wait $pid
    fi
	nohup daemon --name="$NAME" --respawn -f --pidfile=$PIDFILE --output=$BASEPATH/log/${NAME}.log dotnet $BINARY >/dev/null 2>&1 &
}

start pawket-api $BASEPATH/bin/api WalletServer.dll
start pawket-syncer $BASEPATH/bin/syncer NodeDBSyncer.dll
